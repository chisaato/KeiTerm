//go:build windows

package ipc

import (
	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"
	"net"
)

func Listen(path string) (net.Listener, error) {
	token, err := windows.OpenCurrentProcessToken()
	if err != nil {
		return nil, err
	}
	defer token.Close()
	user, err := token.GetTokenUser()
	if err != nil {
		return nil, err
	}
	// Only the current user can access the byte-mode pipe; no Everyone ACL.
	return winio.ListenPipe(path, &winio.PipeConfig{SecurityDescriptor: "D:P(A;;GA;;;" + user.User.Sid.String() + ")", InputBufferSize: 65536, OutputBufferSize: 65536})
}
