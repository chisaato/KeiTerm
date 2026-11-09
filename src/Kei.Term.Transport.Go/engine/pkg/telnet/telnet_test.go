package telnet

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net"
	"strconv"
	"testing"
	"time"
)

func pair(t *testing.T) (*Conn, net.Conn) {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	c, err := Dial(context.Background(), Options{Host: "127.0.0.1", Port: l.Addr().(*net.TCPAddr).Port, TerminalType: "vt100"})
	if err != nil {
		t.Fatal(err)
	}
	p, err := l.Accept()
	if err != nil {
		t.Fatal(err)
	}
	p.SetDeadline(time.Now().Add(5 * time.Second))
	t.Cleanup(func() { c.Close(); p.Close() })
	return c, p
}
func expect(t *testing.T, r io.Reader, want []byte) {
	t.Helper()
	got := make([]byte, len(want))
	if _, err := io.ReadFull(r, got); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}

func TestFragmentedNVTAndEOF(t *testing.T) {
	c, p := pair(t)
	for _, b := range []byte{65, 255, 255, 13, 0, 66, 13, 10, 255, 250, 99, 1, 255, 255, 255, 240, 67} {
		if _, err := p.Write([]byte{b}); err != nil {
			t.Fatal(err)
		}
	}
	expect(t, c, []byte{65, 255, 13, 66, 13, 10, 67})
	p.Close()
	if n, err := c.Read(make([]byte, 1)); n != 0 || err != io.EOF {
		t.Fatalf("%d %v", n, err)
	}
}
func TestNegotiationEchoAndNaws(t *testing.T) {
	c, p := pair(t)
	c.Write([]byte{65, 255, 13})
	expect(t, p, []byte{65, 255, 255, 13, 0})
	expect(t, c, []byte{65, 255, 13})
	p.Write([]byte{255, 251, 1, 255, 251, 1, 255, 251, 77, 255, 251, 77, 255, 253, 77, 255, 253, 77})
	expect(t, p, []byte{255, 253, 1, 255, 254, 77, 255, 252, 77})
	c.Write([]byte{66})
	expect(t, p, []byte{66})
	p.Write([]byte{66, 33})
	expect(t, c, []byte{66, 33})
	p.Write([]byte{255, 252, 1})
	expect(t, p, []byte{255, 254, 1})
	c.Write([]byte{67})
	expect(t, p, []byte{67})
	expect(t, c, []byte{67})
	c.Resize(255, 24)
	p.Write([]byte{255, 253, 31})
	expect(t, p, []byte{255, 251, 31, 255, 250, 31, 0, 255, 255, 0, 24, 255, 240})
	p.Write([]byte{255, 254, 31})
	expect(t, p, []byte{255, 252, 31})
	c.Resize(120, 45)
	p.Write([]byte{255, 253, 31, 255, 253, 24, 255, 250, 24, 1, 255, 240})
	expect(t, p, []byte{255, 251, 31, 255, 250, 31, 0, 120, 0, 45, 255, 240, 255, 251, 24, 255, 250, 24, 0, 118, 116, 49, 48, 48, 255, 240})
}
func TestBinaryDirectionsAndCloseUnblocks(t *testing.T) {
	c, p := pair(t)
	p.Write([]byte{255, 251, 1, 255, 251, 0, 13, 0, 255, 253, 0})
	expect(t, p, []byte{255, 253, 1, 255, 253, 0, 255, 251, 0})
	expect(t, c, []byte{13, 0})
	c.Write([]byte{13, 0, 255})
	expect(t, p, []byte{13, 0, 255, 255})
	p.Write([]byte{255, 252, 0, 255, 254, 0, 13, 0, 65})
	expect(t, p, []byte{255, 254, 0, 255, 252, 0})
	expect(t, c, []byte{13, 65})
	done := make(chan struct{})
	go func() { c.Read(make([]byte, 1)); close(done) }()
	c.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("close blocked")
	}
}

func TestSubnegotiationAtEverySplitAndOverflow(t *testing.T) {
	for split := 1; split < 8; split++ {
		t.Run(strconv.Itoa(split), func(t *testing.T) {
			c, p := pair(t)
			p.Write([]byte{255, 253, 24})
			expect(t, p, []byte{255, 251, 24})
			command := []byte{255, 250, 24, 1, 255, 240, 255, 255}
			p.Write(command[:split])
			p.Write(command[split:])
			expect(t, p, []byte{255, 250, 24, 0, 118, 116, 49, 48, 48, 255, 240})
			expect(t, c, []byte{255})
		})
	}
	c, p := pair(t)
	p.Write(append([]byte{255, 250}, make([]byte, 4097)...))
	if _, err := c.Read(make([]byte, 1)); err == nil || err == io.EOF {
		t.Fatalf("expected parser failure, got %v", err)
	}
}

func TestCanceledDialDoesNotConnect(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	c, err := Dial(ctx, Options{Host: "127.0.0.1", Port: l.Addr().(*net.TCPAddr).Port})
	if c != nil || !errors.Is(err, context.Canceled) {
		t.Fatalf("got %v %v", c, err)
	}
}
