package transport

import (
	"errors"
	"io"
	"sync"
	"time"

	"golang.org/x/crypto/ssh"
)

// Session owns an authenticated client and its jump chain. Shells and SFTP
// clients must be closed by their callers; closing Session terminates them all.
type Session struct {
	client        *ssh.Client
	clients       []*ssh.Client
	once          sync.Once
	closeErr      error
	done          chan struct{}
	keepAliveOnce sync.Once
}

// StartKeepAlive sends global requests without requiring server support/replies.
// Closing the SSH connection also interrupts a blocked request write.
func (s *Session) StartKeepAlive(interval time.Duration) {
	if interval <= 0 {
		return
	}
	s.keepAliveOnce.Do(func() {
		go func() {
			ticker := time.NewTicker(interval)
			defer ticker.Stop()
			for {
				select {
				case <-s.done:
					return
				case <-ticker.C:
					for _, client := range s.clients {
						if _, _, err := client.SendRequest("keepalive@openssh.com", false, nil); err != nil {
							return
						}
					}
				}
			}
		}()
	})
}

// Client exposes the authenticated client for protocol extensions. Ownership
// remains with Session; callers should close Session, not the raw client.
func (s *Session) Client() *ssh.Client { return s.client }

func (s *Session) Close() error {
	s.once.Do(func() {
		if s.done != nil {
			close(s.done)
		}
		for i := len(s.clients) - 1; i >= 0; i-- {
			s.closeErr = errors.Join(s.closeErr, s.clients[i].Close())
		}
	})
	return s.closeErr
}

type PTYOptions struct {
	Term          string
	Columns       int
	Rows          int
	Modes         ssh.TerminalModes
	Env           map[string]string
	StartupScript string
}

type shellSession interface {
	StdinPipe() (io.WriteCloser, error)
	StdoutPipe() (io.Reader, error)
	StderrPipe() (io.Reader, error)
	RequestPty(string, int, int, ssh.TerminalModes) error
	Shell() error
	Setenv(string, string) error
	WindowChange(int, int) error
	Wait() error
	Close() error
}

// Shell implements io.ReadWriteCloser. Stderr is exposed separately so it cannot
// block stdout; callers must drain both streams (PTY servers usually merge them).
type Shell struct {
	session  shellSession
	stdin    io.WriteCloser
	stdout   io.Reader
	stderr   io.Reader
	once     sync.Once
	closeErr error
}

func (s *Session) OpenShell(options PTYOptions) (*Shell, error) {
	if s.client == nil {
		return nil, errors.New("session has no SSH client")
	}
	session, err := s.client.NewSession()
	if err != nil {
		return nil, err
	}
	return openShell(session, options)
}

func openShell(session shellSession, options PTYOptions) (_ *Shell, err error) {
	defer func() {
		if err != nil {
			_ = session.Close()
		}
	}()
	if options.Columns <= 0 || options.Rows <= 0 {
		return nil, errors.New("PTY dimensions must be positive")
	}
	if options.Term == "" {
		options.Term = "xterm-256color"
	}
	shell := &Shell{session: session}
	if shell.stdin, err = session.StdinPipe(); err != nil {
		return nil, err
	}
	if shell.stdout, err = session.StdoutPipe(); err != nil {
		return nil, err
	}
	if shell.stderr, err = session.StderrPipe(); err != nil {
		return nil, err
	}
	if err = session.RequestPty(options.Term, options.Rows, options.Columns, options.Modes); err != nil {
		return nil, err
	}
	for key, value := range options.Env {
		if err = session.Setenv(key, value); err != nil {
			return nil, err
		}
	}
	if err = session.Shell(); err != nil {
		return nil, err
	}
	if options.StartupScript != "" {
		if _, err = io.WriteString(shell.stdin, options.StartupScript+"\n"); err != nil {
			return nil, err
		}
	}
	return shell, nil
}

func (s *Shell) Read(p []byte) (int, error)  { return s.stdout.Read(p) }
func (s *Shell) Write(p []byte) (int, error) { return s.stdin.Write(p) }
func (s *Shell) Stderr() io.Reader           { return s.stderr }
func (s *Shell) CloseWrite() error           { return s.stdin.Close() }
func (s *Shell) Wait() error                 { return s.session.Wait() }
func (s *Shell) Resize(columns, rows int) error {
	if columns <= 0 || rows <= 0 {
		return errors.New("PTY dimensions must be positive")
	}
	return s.session.WindowChange(rows, columns)
}
func (s *Shell) Close() error {
	s.once.Do(func() { s.closeErr = s.session.Close() })
	return s.closeErr
}
