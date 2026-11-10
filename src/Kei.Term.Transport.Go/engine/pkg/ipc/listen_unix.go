//go:build !windows

package ipc

import (
	"net"
	"os"
)

// Listen refuses to replace existing filesystem entries, including stale sockets.
// Callers must place the socket inside a directory private to the current user.
func Listen(path string) (net.Listener, error) {
	if _, err := os.Lstat(path); err == nil {
		return nil, os.ErrExist
	} else if !os.IsNotExist(err) {
		return nil, err
	}
	l, err := net.ListenUnix("unix", &net.UnixAddr{Name: path, Net: "unix"})
	if err != nil {
		return nil, err
	}
	l.SetUnlinkOnClose(true)
	if err = os.Chmod(path, 0600); err != nil {
		_ = l.Close()
		return nil, err
	}
	return l, nil
}
