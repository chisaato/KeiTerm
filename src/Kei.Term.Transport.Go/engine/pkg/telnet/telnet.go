// Package telnet provides a bounded, streaming Telnet terminal over TCP.
package telnet

import (
	"context"
	"errors"
	"io"
	"net"
	"strconv"
	"sync"
	"time"
)

type Options struct {
	Host                string `json:"host"`
	Port                int    `json:"port"`
	TerminalType        string `json:"terminalType"`
	TimeoutMilliseconds int    `json:"timeoutMilliseconds"`
}

type Conn struct {
	conn                                         net.Conn
	mu                                           sync.Mutex
	local, remote, localRejected, remoteRejected [256]bool
	state                                        int
	command                                      byte
	sub                                          []byte
	cr                                           bool
	cols, rows                                   int
	output                                       chan []byte
	done                                         chan struct{}
	once                                         sync.Once
	err                                          error
	current                                      []byte
}

func Dial(ctx context.Context, o Options) (*Conn, error) {
	if o.Port == 0 {
		o.Port = 23
	}
	if o.TerminalType == "" {
		o.TerminalType = "xterm-256color"
	}
	if o.Host == "" || o.Port < 1 || o.Port > 65535 || len(o.TerminalType) > 255 {
		return nil, errors.New("invalid Telnet options")
	}
	for _, b := range []byte(o.TerminalType) {
		if b < 32 || b > 126 {
			return nil, errors.New("terminalType must be printable ASCII")
		}
	}
	timeout := 30 * time.Second
	if o.TimeoutMilliseconds > 0 {
		timeout = time.Duration(o.TimeoutMilliseconds) * time.Millisecond
	}
	socket, err := (&net.Dialer{Timeout: timeout}).DialContext(ctx, "tcp", net.JoinHostPort(o.Host, strconv.Itoa(o.Port)))
	if err != nil {
		return nil, err
	}
	c := &Conn{conn: socket, cols: 80, rows: 24, output: make(chan []byte, 128), done: make(chan struct{})}
	go c.receive(o.TerminalType)
	return c, nil
}
func (c *Conn) end(err error) { c.once.Do(func() { c.err = err; c.conn.Close(); close(c.done) }) }
func (c *Conn) Close() error  { c.end(io.EOF); return nil }
func (c *Conn) publish(b []byte) error {
	if len(b) == 0 {
		return nil
	}
	select {
	case c.output <- b:
		return nil
	default:
		return errors.New("unconsumed Telnet output exceeds buffer limit")
	}
}
func (c *Conn) Read(b []byte) (int, error) {
	if len(b) == 0 {
		return 0, nil
	}
	for len(c.current) == 0 {
		select {
		case c.current = <-c.output:
		default:
			select {
			case c.current = <-c.output:
			case <-c.done:
				select {
				case c.current = <-c.output:
				default:
					return 0, c.err
				}
			}
		}
	}
	n := copy(b, c.current)
	c.current = c.current[n:]
	return n, nil
}
func (c *Conn) wire(b []byte) error {
	c.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	defer c.conn.SetWriteDeadline(time.Time{})
	for len(b) > 0 {
		n, err := c.conn.Write(b)
		if err != nil {
			return err
		}
		b = b[n:]
	}
	return nil
}
func (c *Conn) Write(b []byte) (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	out := make([]byte, 0, len(b)*2)
	for i, v := range b {
		out = append(out, v)
		if v == 255 {
			out = append(out, 255)
		}
		if !c.local[0] && v == 13 && (i+1 == len(b) || b[i+1] != 10) {
			out = append(out, 0)
		}
	}
	err := c.wire(out)
	if err == nil && !c.remote[1] {
		err = c.publish(append([]byte(nil), b...))
	}
	if err != nil {
		c.end(err)
		return 0, err
	}
	return len(b), nil
}
func (c *Conn) Resize(cols, rows int) error {
	if cols < 1 || cols > 65535 || rows < 1 || rows > 65535 {
		return errors.New("invalid terminal dimensions")
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.cols, c.rows = cols, rows
	if c.local[31] {
		return c.window()
	}
	return nil
}
func (c *Conn) subneg(option byte, payload []byte) error {
	b := []byte{255, 250, option}
	for _, v := range payload {
		b = append(b, v)
		if v == 255 {
			b = append(b, 255)
		}
	}
	return c.wire(append(b, 255, 240))
}
func (c *Conn) window() error {
	return c.subneg(31, []byte{byte(c.cols >> 8), byte(c.cols), byte(c.rows >> 8), byte(c.rows)})
}
func (c *Conn) negotiate(command, option byte) error {
	local := command == 253 || command == 254
	positive := command == 251 || command == 253
	enabled, rejected := &c.remote, &c.remoteRejected
	yes, no := byte(253), byte(254)
	supported := option == 0 || option == 1 || option == 3
	if local {
		enabled, rejected = &c.local, &c.localRejected
		yes, no = 251, 252
		supported = option == 0 || option == 3 || option == 24 || option == 31
	}
	if positive {
		if enabled[option] {
			return nil
		}
		if !supported {
			if rejected[option] {
				return nil
			}
			rejected[option] = true
			return c.wire([]byte{255, no, option})
		}
		enabled[option] = true
		rejected[option] = false
		if !local && option == 0 {
			c.cr = false
		}
		if err := c.wire([]byte{255, yes, option}); err != nil {
			return err
		}
		if local && option == 31 {
			return c.window()
		}
	} else {
		rejected[option] = false
		if enabled[option] {
			enabled[option] = false
			return c.wire([]byte{255, no, option})
		}
	}
	return nil
}
func (c *Conn) data(b byte, out *[]byte) {
	if c.cr {
		c.cr = false
		if b == 0 {
			return
		}
	}
	*out = append(*out, b)
	if !c.remote[0] && b == 13 {
		c.cr = true
	}
}
func (c *Conn) parse(b byte, out *[]byte, term string) error {
	switch c.state {
	case 0:
		if b == 255 {
			c.state = 1
		} else {
			c.data(b, out)
		}
	case 1:
		switch {
		case b == 255:
			c.data(b, out)
			c.state = 0
		case b >= 251 && b <= 254:
			c.command = b
			c.state = 2
		case b == 250:
			c.sub = nil
			c.state = 3
		default:
			c.state = 0
		}
	case 2:
		c.state = 0
		return c.negotiate(c.command, b)
	case 3:
		if b == 255 {
			c.state = 4
		} else {
			c.sub = append(c.sub, b)
		}
	case 4:
		if b == 255 {
			c.sub = append(c.sub, b)
			c.state = 3
		} else if b == 240 {
			c.state = 0
			if len(c.sub) == 2 && c.sub[0] == 24 && c.sub[1] == 1 && c.local[24] {
				return c.subneg(24, append([]byte{0}, []byte(term)...))
			}
		} else {
			c.state = 3
		}
	}
	if len(c.sub) > 4096 {
		return errors.New("Telnet subnegotiation exceeds 4096 bytes")
	}
	return nil
}
func (c *Conn) receive(term string) {
	buffer := make([]byte, 8192)
	for {
		n, err := c.conn.Read(buffer)
		c.mu.Lock()
		out := make([]byte, 0, n)
		for _, b := range buffer[:n] {
			if e := c.parse(b, &out, term); e != nil {
				err = e
				break
			}
		}
		if e := c.publish(out); e != nil {
			err = e
		}
		c.mu.Unlock()
		if err != nil {
			c.end(err)
			return
		}
	}
}
