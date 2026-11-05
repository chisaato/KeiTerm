package transport

import (
	"context"
	"errors"
	"fmt"
	"net"
	"time"

	"golang.org/x/crypto/ssh"
)

// Endpoint is a single hop. HostKeyCallback is mandatory, including on jumps.
type Endpoint struct {
	Address         string
	User            string
	Auth            Auth
	HostKeyCallback ssh.HostKeyCallback
	Timeout         time.Duration
}

// Dialer allows a custom network transport without depending on any platform API.
type Dialer struct {
	DialContext func(context.Context, string, string) (net.Conn, error)
}

// Dial connects through jumps in outermost-to-innermost order. The returned
// Session owns every connection; closing it also closes all jump clients.
func (d Dialer) Dial(ctx context.Context, target Endpoint, jumps ...Endpoint) (*Session, error) {
	endpoints := append(append([]Endpoint(nil), jumps...), target)
	configs := make([]*ssh.ClientConfig, len(endpoints))
	// Reject invalid configuration before opening any network connections.
	for i, endpoint := range endpoints {
		if endpoint.Address == "" || endpoint.User == "" || endpoint.HostKeyCallback == nil {
			return nil, fmt.Errorf("hop %d: address, user and host key callback are required", i)
		}
		methods, err := endpoint.Auth.Methods()
		if err != nil {
			return nil, fmt.Errorf("hop %d: %w", i, err)
		}
		configs[i] = &ssh.ClientConfig{User: endpoint.User, Auth: methods, HostKeyCallback: endpoint.HostKeyCallback}
	}
	clients := make([]*ssh.Client, 0, len(endpoints))
	cleanup := func() {
		for i := len(clients) - 1; i >= 0; i-- {
			_ = clients[i].Close()
		}
	}
	for i, endpoint := range endpoints {
		hopCtx := ctx
		cancel := func() {}
		if endpoint.Timeout > 0 {
			hopCtx, cancel = context.WithTimeout(ctx, endpoint.Timeout)
		}
		var conn net.Conn
		var err error
		if len(clients) == 0 {
			dial := d.DialContext
			if dial == nil {
				dial = (&net.Dialer{}).DialContext
			}
			conn, err = dial(hopCtx, "tcp", endpoint.Address)
		} else {
			conn, err = clients[len(clients)-1].DialContext(hopCtx, "tcp", endpoint.Address)
		}
		if err == nil {
			var client *ssh.Client
			client, err = handshake(hopCtx, conn, endpoint.Address, configs[i])
			if err == nil {
				clients = append(clients, client)
			}
		}
		cancel()
		if err != nil {
			cleanup()
			return nil, fmt.Errorf("hop %d (%s): %w", i, endpoint.Address, err)
		}
	}
	return &Session{client: clients[len(clients)-1], clients: clients}, nil
}

func handshake(ctx context.Context, conn net.Conn, address string, config *ssh.ClientConfig) (*ssh.Client, error) {
	// Closing the underlying connection interrupts a stalled SSH handshake even
	// for tunneled connections that do not support deadlines.
	done := make(chan struct{})
	stop := context.AfterFunc(ctx, func() { _ = conn.Close(); close(done) })
	c, channels, requests, err := ssh.NewClientConn(conn, address, config)
	if !stop() {
		<-done
	}
	if ctx.Err() != nil {
		_ = conn.Close()
		return nil, ctx.Err()
	}
	if err != nil {
		return nil, errors.Join(err, conn.Close())
	}
	return ssh.NewClient(c, channels, requests), nil
}
