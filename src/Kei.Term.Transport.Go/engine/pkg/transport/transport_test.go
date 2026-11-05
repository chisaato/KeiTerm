package transport

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/pkg/sftp"
	"golang.org/x/crypto/ssh"
)

func testKey(t *testing.T) (ed25519.PrivateKey, ssh.Signer) {
	t.Helper()
	_, key, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := ssh.NewSignerFromKey(key)
	if err != nil {
		t.Fatal(err)
	}
	return key, signer
}

// A real SSH protocol peer with in-memory SFTP storage and TCP forwarding.
// It needs neither sshd nor the filesystem and exercises production transports.
func startPeer(t *testing.T, acceptedKey ssh.PublicKey) (Endpoint, *atomic.Int32) {
	t.Helper()
	_, host := testKey(t)
	config := &ssh.ServerConfig{
		PasswordCallback: func(_ ssh.ConnMetadata, password []byte) (*ssh.Permissions, error) {
			if string(password) != "secret" {
				return nil, errors.New("bad password")
			}
			return nil, nil
		},
		PublicKeyCallback: func(_ ssh.ConnMetadata, key ssh.PublicKey) (*ssh.Permissions, error) {
			if acceptedKey == nil || !bytes.Equal(key.Marshal(), acceptedKey.Marshal()) {
				return nil, errors.New("bad key")
			}
			return nil, nil
		},
	}
	config.AddHostKey(host)
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var active atomic.Int32
	var workers sync.WaitGroup
	var mu sync.Mutex
	var connections []net.Conn
	acceptDone := make(chan struct{})
	go func() {
		defer close(acceptDone)
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			mu.Lock()
			connections = append(connections, conn)
			mu.Unlock()
			workers.Add(1)
			go func() {
				defer workers.Done()
				defer conn.Close()
				server, channels, requests, err := ssh.NewServerConn(conn, config)
				if err != nil {
					return
				}
				active.Add(1)
				defer active.Add(-1)
				defer server.Close()
				go ssh.DiscardRequests(requests)
				var handlers sync.WaitGroup
				defer handlers.Wait()
				for incoming := range channels {
					handlers.Add(1)
					go func() {
						defer handlers.Done()
						switch incoming.ChannelType() {
						case "session":
							channel, requests, err := incoming.Accept()
							if err != nil {
								return
							}
							defer channel.Close()
							for request := range requests {
								var subsystem struct{ Name string }
								if request.Type != "subsystem" || ssh.Unmarshal(request.Payload, &subsystem) != nil || subsystem.Name != "sftp" {
									_ = request.Reply(false, nil)
									continue
								}
								_ = request.Reply(true, nil)
								server := sftp.NewRequestServer(channel, sftp.InMemHandler())
								_ = server.Serve()
								_ = server.Close()
								return
							}
						case "direct-tcpip":
							var destination struct {
								Host       string
								Port       uint32
								Origin     string
								OriginPort uint32
							}
							if ssh.Unmarshal(incoming.ExtraData(), &destination) != nil {
								_ = incoming.Reject(ssh.ConnectionFailed, "bad destination")
								return
							}
							remote, err := net.DialTimeout("tcp", net.JoinHostPort(destination.Host, fmt.Sprint(destination.Port)), time.Second)
							if err != nil {
								_ = incoming.Reject(ssh.ConnectionFailed, "unreachable")
								return
							}
							defer remote.Close()
							channel, requests, err := incoming.Accept()
							if err != nil {
								return
							}
							defer channel.Close()
							go ssh.DiscardRequests(requests)
							copied := make(chan struct{})
							go func() { _, _ = io.Copy(channel, remote); _ = channel.Close(); close(copied) }()
							_, _ = io.Copy(remote, channel)
							_ = remote.Close()
							<-copied
						default:
							_ = incoming.Reject(ssh.UnknownChannelType, "unsupported")
						}
					}()
				}
			}()
		}
	}()
	t.Cleanup(func() {
		_ = listener.Close()
		<-acceptDone
		mu.Lock()
		for _, conn := range connections {
			_ = conn.Close()
		}
		mu.Unlock()
		workers.Wait()
	})
	password := "secret"
	return Endpoint{Address: listener.Addr().String(), User: "tester", Auth: Auth{Password: &password}, HostKeyCallback: ssh.FixedHostKey(host.PublicKey()), Timeout: 3 * time.Second}, &active
}

func TestAuthentication(t *testing.T) {
	key, signer := testKey(t)
	plain, err := ssh.MarshalPrivateKey(key, "test")
	if err != nil {
		t.Fatal(err)
	}
	encrypted, err := ssh.MarshalPrivateKeyWithPassphrase(key, "test", []byte("passphrase"))
	if err != nil {
		t.Fatal(err)
	}
	endpoint, _ := startPeer(t, signer.PublicKey())
	otherKey, _ := testKey(t)
	otherPEM, err := ssh.MarshalPrivateKey(otherKey, "unaccepted")
	if err != nil {
		t.Fatal(err)
	}
	for name, auth := range map[string]Auth{
		"password":             endpoint.Auth,
		"private key":          {PrivateKey: pem.EncodeToMemory(plain)},
		"encrypted key":        {PrivateKey: pem.EncodeToMemory(encrypted), Passphrase: []byte("passphrase")},
		"agent hook":           {Signers: func() ([]ssh.Signer, error) { return []ssh.Signer{signer}, nil }},
		"local key then agent": {PrivateKey: pem.EncodeToMemory(otherPEM), Signers: func() ([]ssh.Signer, error) { return []ssh.Signer{signer}, nil }},
	} {
		t.Run(name, func(t *testing.T) {
			target := endpoint
			target.Auth = auth
			session, err := (Dialer{}).Dial(context.Background(), target)
			if err != nil {
				t.Fatal(err)
			}
			_ = session.Close()
		})
	}
	for name, auth := range map[string]Auth{
		"empty":            {},
		"malformed key":    {PrivateKey: []byte("not a key")},
		"wrong passphrase": {PrivateKey: pem.EncodeToMemory(encrypted), Passphrase: []byte("wrong")},
		"nil extension":    {Additional: []ssh.AuthMethod{nil}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := auth.Methods(); err == nil {
				t.Fatal("expected invalid authentication to fail")
			}
		})
	}
	t.Run("agent error", func(t *testing.T) {
		target := endpoint
		target.Auth = Auth{Signers: func() ([]ssh.Signer, error) { return nil, errors.New("agent unavailable") }}
		if _, err := (Dialer{}).Dial(context.Background(), target); err == nil {
			t.Fatal("agent failure was ignored")
		}
	})
}

func TestMultiHopAndConcurrentSFTP(t *testing.T) {
	jump1, active1 := startPeer(t, nil)
	jump2, active2 := startPeer(t, nil)
	target, activeTarget := startPeer(t, nil)
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	session, err := (Dialer{}).Dial(ctx, target, jump1, jump2)
	if err != nil {
		t.Fatal(err)
	}
	defer session.Close()
	fs, err := session.OpenSFTP()
	if err != nil {
		t.Fatal(err)
	}
	defer fs.Close()
	if err := fs.Mkdir("/transfers"); err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	for i := range 12 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			path := fmt.Sprintf("/transfers/%d", i)
			data := bytes.Repeat([]byte{byte(i), 0, 255}, 128*1024)
			writer, err := fs.Write(path)
			if err != nil {
				t.Error(err)
				return
			}
			_, copyErr := io.Copy(writer, bytes.NewReader(data))
			if err := errors.Join(copyErr, writer.Close()); err != nil {
				t.Error(err)
				return
			}
			info, err := fs.Stat(path)
			if err != nil {
				t.Error(err)
				return
			}
			if info.Size() != int64(len(data)) {
				t.Errorf("size = %d", info.Size())
			}
			reader, err := fs.Read(path)
			if err != nil {
				t.Error(err)
				return
			}
			got, readErr := io.ReadAll(reader)
			if err := errors.Join(readErr, reader.Close()); err != nil {
				t.Error(err)
				return
			}
			if !bytes.Equal(got, data) {
				t.Error("stream corrupted")
			}
		}()
	}
	wg.Wait()
	entries, err := fs.ListDir("/transfers")
	if err != nil || len(entries) != 12 {
		t.Fatalf("ListDir: %d entries, %v", len(entries), err)
	}
	if err := fs.Rename("/transfers/0", "/transfers/renamed"); err != nil {
		t.Fatal(err)
	}
	if _, err := fs.Stat("/transfers/0"); err == nil {
		t.Fatal("old path still exists")
	}
	// Reopening a file for Write must truncate it, not leave a stale tail.
	w, err := fs.Write("/transfers/renamed")
	if err != nil {
		t.Fatal(err)
	}
	_, writeErr := w.Write([]byte("short"))
	if err := errors.Join(writeErr, w.Close()); err != nil {
		t.Fatal(err)
	}
	info, err := fs.Stat("/transfers/renamed")
	if err != nil || info.Size() != 5 {
		t.Fatalf("truncate: %v, %v", info, err)
	}
	if err := fs.Remove("/transfers/renamed"); err != nil {
		t.Fatal(err)
	}
	if _, err := fs.Read("/missing"); err == nil {
		t.Fatal("missing file read succeeded")
	}
	_ = fs.Close()
	_ = session.Close()
	_ = session.Close()
	deadline := time.Now().Add(2 * time.Second)
	for active1.Load()+active2.Load()+activeTarget.Load() != 0 {
		if time.Now().After(deadline) {
			t.Fatal("jump connections leaked")
		}
		time.Sleep(time.Millisecond)
	}
}

func TestDialerRejectsHostKeyAndCleansFailedChain(t *testing.T) {
	jump, active := startPeer(t, nil)
	target, _ := startPeer(t, nil)
	target.HostKeyCallback = func(string, net.Addr, ssh.PublicKey) error { return errors.New("untrusted host") }
	if _, err := (Dialer{}).Dial(context.Background(), target, jump); err == nil || !strings.Contains(err.Error(), "untrusted host") {
		t.Fatalf("host key error = %v", err)
	}
	deadline := time.Now().Add(time.Second)
	for active.Load() != 0 {
		if time.Now().After(deadline) {
			t.Fatal("failed chain leaked jump")
		}
		time.Sleep(time.Millisecond)
	}
	target.HostKeyCallback = nil
	called := false
	dialer := Dialer{DialContext: func(context.Context, string, string) (net.Conn, error) {
		called = true
		return nil, errors.New("unexpected dial")
	}}
	if _, err := dialer.Dial(context.Background(), target, jump); err == nil || called {
		t.Fatal("invalid config was not rejected before dialing")
	}
}

func TestDialerHandshakeCancellation(t *testing.T) {
	endpoint, _ := startPeer(t, nil)
	endpoint.Timeout = 25 * time.Millisecond
	client, server := net.Pipe()
	defer server.Close()
	dialer := Dialer{DialContext: func(context.Context, string, string) (net.Conn, error) { return client, nil }}
	_, err := dialer.Dial(context.Background(), endpoint)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("expected handshake timeout, got %v", err)
	}
	if _, err := server.Write([]byte("x")); err == nil {
		t.Fatal("canceled connection remained open")
	}
}

type memoryShell struct {
	input         bytes.Buffer
	output        *strings.Reader
	closed        int
	rows, columns int
	term          string
	fail          string
	env           map[string]string
}

type nopWriteCloser struct{ io.Writer }

func (nopWriteCloser) Close() error { return nil }
func (m *memoryShell) StdinPipe() (io.WriteCloser, error) {
	if m.fail == "stdin" {
		return nil, errors.New("stdin")
	}
	return nopWriteCloser{&m.input}, nil
}
func (m *memoryShell) StdoutPipe() (io.Reader, error) {
	if m.fail == "stdout" {
		return nil, errors.New("stdout")
	}
	return m.output, nil
}
func (m *memoryShell) StderrPipe() (io.Reader, error) {
	if m.fail == "stderr" {
		return nil, errors.New("stderr")
	}
	return strings.NewReader("diagnostic"), nil
}
func (m *memoryShell) RequestPty(term string, rows, columns int, _ ssh.TerminalModes) error {
	m.term, m.rows, m.columns = term, rows, columns
	if m.fail == "pty" {
		return errors.New("pty")
	}
	return nil
}
func (m *memoryShell) Shell() error {
	if m.fail == "shell" {
		return errors.New("shell")
	}
	return nil
}
func (m *memoryShell) Setenv(key, value string) error {
	if m.fail == "env" {
		return errors.New("env rejected")
	}
	if m.env == nil {
		m.env = make(map[string]string)
	}
	m.env[key] = value
	return nil
}
func (m *memoryShell) WindowChange(rows, columns int) error {
	m.rows, m.columns = rows, columns
	return nil
}
func (m *memoryShell) Wait() error  { return errors.New("remote exit") }
func (m *memoryShell) Close() error { m.closed++; return nil }

func TestShellStreamsResizeAndClose(t *testing.T) {
	peer := &memoryShell{output: strings.NewReader("terminal output")}
	shell, err := openShell(peer, PTYOptions{Columns: 80, Rows: 24})
	if err != nil {
		t.Fatal(err)
	}
	if peer.term != "xterm-256color" || peer.rows != 24 || peer.columns != 80 {
		t.Fatal("incorrect PTY request")
	}
	if _, err := io.WriteString(shell, "command\n"); err != nil {
		t.Fatal(err)
	}
	if peer.input.String() != "command\n" {
		t.Fatal("input lost")
	}
	output, err := io.ReadAll(shell)
	if err != nil || string(output) != "terminal output" {
		t.Fatalf("output = %q, %v", output, err)
	}
	stderr, err := io.ReadAll(shell.Stderr())
	if err != nil || string(stderr) != "diagnostic" {
		t.Fatalf("stderr = %q, %v", stderr, err)
	}
	if err := shell.Resize(120, 40); err != nil {
		t.Fatal(err)
	}
	if peer.rows != 40 || peer.columns != 120 {
		t.Fatal("resize dimensions reversed")
	}
	if err := shell.Resize(0, 40); err == nil {
		t.Fatal("invalid resize accepted")
	}
	if peer.columns != 120 {
		t.Fatal("invalid resize reached server")
	}
	if err := shell.Wait(); err == nil {
		t.Fatal("remote exit status lost")
	}
	_ = shell.Close()
	_ = shell.Close()
	if peer.closed != 1 {
		t.Fatalf("closed %d times", peer.closed)
	}
}

func TestShellStartupFailureClosesSession(t *testing.T) {
	for _, failure := range []string{"stdin", "stdout", "stderr", "pty", "shell", "dimensions", "env"} {
		t.Run(failure, func(t *testing.T) {
			peer := &memoryShell{fail: failure}
			options := PTYOptions{Columns: 80, Rows: 24, Env: map[string]string{"LANG": "C"}}
			if failure == "dimensions" {
				options.Rows = -1
			}
			if _, err := openShell(peer, options); err == nil {
				t.Fatal("expected failure")
			}
			if peer.closed != 1 {
				t.Fatalf("failed session closed %d times", peer.closed)
			}
		})
	}
}

func TestShellEnvironmentAndStartupScript(t *testing.T) {
	peer := &memoryShell{output: strings.NewReader("")}
	shell, err := openShell(peer, PTYOptions{Columns: 80, Rows: 24, Env: map[string]string{"LANG": "C.UTF-8"}, StartupScript: "cd /work\nprintf ready"})
	if err != nil {
		t.Fatal(err)
	}
	defer shell.Close()
	if peer.env["LANG"] != "C.UTF-8" || peer.input.String() != "cd /work\nprintf ready\n" {
		t.Fatalf("env/script lost: %v %q", peer.env, peer.input.String())
	}
}
