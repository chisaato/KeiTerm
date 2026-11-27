package transport

import (
	"errors"
	"io"
	"os"
	"sync"

	"github.com/pkg/sftp"
)

// SFTP multiplexes concurrent operations over one SSH subsystem. Each Read/Write
// returns an independent streaming handle; no whole-file buffering or global
// transfer lock is used. The caller owns and must close every returned handle.
type SFTP struct {
	client   *sftp.Client
	once     sync.Once
	closeErr error
}

func (s *Session) OpenSFTP() (*SFTP, error) {
	if s.client == nil {
		return nil, errors.New("session has no SSH client")
	}
	client, err := sftp.NewClient(s.client, sftp.UseConcurrentReads(true), sftp.UseConcurrentWrites(true))
	if err != nil {
		return nil, err
	}
	return &SFTP{client: client}, nil
}

func (s *SFTP) ListDir(path string) ([]os.FileInfo, error) { return s.client.ReadDir(path) }
func (s *SFTP) Stat(path string) (os.FileInfo, error)      { return s.client.Stat(path) }
func (s *SFTP) Chown(path string, uid, gid int) error      { return s.client.Chown(path, uid, gid) }
func (s *SFTP) Lstat(path string) (os.FileInfo, error)     { return s.client.Lstat(path) }
func (s *SFTP) Chmod(path string, mode os.FileMode) error  { return s.client.Chmod(path, mode) }
func (s *SFTP) Read(path string) (io.ReadCloser, error)    { return s.client.Open(path) }

// Write creates or truncates a file. Concurrent writes to the same path require
// coordination by the caller; different paths may be transferred concurrently.
func (s *SFTP) Write(path string) (io.WriteCloser, error) { return s.client.Create(path) }
func (s *SFTP) Mkdir(path string) error                   { return s.client.Mkdir(path) }

// Remove removes a file or an empty directory; it never recursively deletes.
func (s *SFTP) Remove(path string) error { return s.client.Remove(path) }

// Rename uses portable SFTP rename semantics, without implicit overwrite.
func (s *SFTP) Rename(oldPath, newPath string) error { return s.client.Rename(oldPath, newPath) }
func (s *SFTP) RenameOverwrite(oldPath, newPath string) error {
	if _, supported := s.client.HasExtension("posix-rename@openssh.com"); supported {
		return s.client.PosixRename(oldPath, newPath)
	}
	// Standard SFTP rename can publish a new name, but must never delete an existing destination.
	if _, err := s.client.Lstat(newPath); !errors.Is(err, os.ErrNotExist) {
		if err != nil {
			return err
		}
		return errors.New("SFTP server does not support atomic overwrite")
	}
	return s.client.Rename(oldPath, newPath)
}

func (s *SFTP) Close() error {
	s.once.Do(func() { s.closeErr = s.client.Close() })
	return s.closeErr
}
