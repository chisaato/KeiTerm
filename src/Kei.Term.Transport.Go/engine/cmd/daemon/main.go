package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"github.com/keiterm/transport-engine/pkg/ipc"
)

func main() {
	path := flag.String("socket", "", "Unix socket path or Windows \\\\.\\pipe\\name")
	idle := flag.Duration("idle-timeout", 60*time.Second, "exit after this long without connected clients")
	handshake := flag.Duration("handshake-timeout", 15*time.Second, "maximum wait for initial IPC traffic")
	parent := flag.Int("parent-pid", os.Getppid(), "exit when parent PID changes (0 disables)")
	flag.Parse()
	if *path == "" || *idle <= 0 || *handshake <= 0 {
		fmt.Fprintln(os.Stderr, "-socket and positive timeouts required")
		os.Exit(2)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if err := run(ctx, cancel, *path, *idle, *handshake, *parent); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(ctx context.Context, cancel context.CancelFunc, path string, idle, handshake time.Duration, parent int) error {
	listener, err := ipc.Listen(path)
	if err != nil {
		return err
	}
	defer listener.Close()
	var mu sync.Mutex
	active := 0
	last := time.Now()
	var wg sync.WaitGroup
	diagnostics := log.New(os.Stderr, "", 0)
	var connectionID uint64
	stop := context.AfterFunc(ctx, func() { _ = listener.Close() })
	defer stop()
	go func() {
		ticker := time.NewTicker(min(time.Second, idle))
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				mu.Lock()
				expired := active == 0 && time.Since(last) >= idle
				mu.Unlock()
				if expired || (parent > 0 && !parentAlive(parent)) {
					cancel()
					return
				}
			}
		}
	}()
	defer wg.Wait()
	for {
		conn, err := listener.Accept()
		if err != nil {
			if ctx.Err() != nil {
				return nil
			}
			cancel()
			return err
		}
		mu.Lock()
		active++
		mu.Unlock()
		_ = conn.SetReadDeadline(time.Now().Add(handshake))
		wg.Add(1)
		connectionID++
		go func(id uint64) {
			defer wg.Done()
			defer func() { mu.Lock(); active--; last = time.Now(); mu.Unlock() }()
			// The initial deadline is cleared only after a complete first frame;
			// partial headers cannot keep an orphan daemon alive indefinitely.
			wrapped := &firstFrameConn{Conn: conn, remaining: 9}
			serveConnection(ctx, wrapped, &ipc.Router{Shutdown: cancel}, diagnostics, id)
		}(connectionID)
	}
}

// 只输出固定原因和本地连接编号，禁止把错误原文或 RPC/SSH 数据写进日志。
func serveConnection(ctx context.Context, conn net.Conn, router *ipc.Router, diagnostics *log.Logger, id uint64) {
	err := router.Serve(ctx, conn)
	reason := "protocol"
	var timeout net.Error
	switch {
	case ctx.Err() != nil:
		reason = "canceled"
	case errors.Is(err, io.EOF), errors.Is(err, io.ErrUnexpectedEOF):
		reason = "eof"
	case errors.As(err, &timeout) && timeout.Timeout():
		reason = "timeout"
	case errors.As(err, &timeout):
		reason = "io"
	}
	diagnostics.Printf(`{"event":"router-ended","connection":%d,"reason":"%s"}`, id, reason)
}

type firstFrameConn struct {
	net.Conn
	remaining int
	header    [9]byte
	read      int
	payload   bool
}

func (c *firstFrameConn) Read(p []byte) (int, error) {
	if c.remaining == 0 {
		return c.Conn.Read(p)
	}
	n, err := c.Conn.Read(p[:min(len(p), c.remaining)])
	if !c.payload {
		copy(c.header[c.read:], p[:n])
		c.read += n
	}
	c.remaining -= n
	if c.remaining == 0 {
		if !c.payload {
			c.payload = true
			c.remaining = int(uint32(c.header[5])<<24 | uint32(c.header[6])<<16 | uint32(c.header[7])<<8 | uint32(c.header[8]))
		}
		if c.remaining == 0 {
			_ = c.Conn.SetReadDeadline(time.Time{})
		}
	}
	return n, err
}
