package serial

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"

	"golang.org/x/sys/unix"
)

func pair(t *testing.T) (string, *os.File, *exec.Cmd) {
	t.Helper()
	if _, err := exec.LookPath("socat"); err != nil {
		t.Skip("socat unavailable")
	}
	d := t.TempDir()
	a, b := filepath.Join(d, "a"), filepath.Join(d, "b")
	c := exec.Command("socat", "pty,raw,echo=0,link="+a, "pty,raw,echo=0,link="+b)
	var log bytes.Buffer
	c.Stderr = &log
	if err := c.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = c.Process.Kill(); _ = c.Wait(); t.Log(log.String()) })
	deadline := time.Now().Add(3 * time.Second)
	for {
		_, e1 := os.Stat(a)
		_, e2 := os.Stat(b)
		if e1 == nil && e2 == nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("socat PTY startup timeout")
		}
		time.Sleep(10 * time.Millisecond)
	}
	h, err := unix.Open(b, unix.O_RDWR|unix.O_NOCTTY|unix.O_NONBLOCK, 0)
	if err != nil {
		t.Fatal(err)
	}
	f := os.NewFile(uintptr(h), b)
	t.Cleanup(func() { _ = f.Close() })
	return a, f, c
}

func await(t *testing.T, done <-chan error) error {
	t.Helper()
	select {
	case err := <-done:
		return err
	case <-time.After(3 * time.Second):
		t.Fatal("operation did not terminate")
		return nil
	}
}

func TestPTYRoundTripCloseReopen(t *testing.T) {
	name, peer, _ := pair(t)
	for i := 0; i < 2; i++ {
		p, err := Open(context.Background(), Options{PortName: name})
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { _ = p.Close() })
		payload := []byte{0, 255, 13, 10, 17, 19, 'x'}
		if _, err = p.Write(payload); err != nil {
			t.Fatal(err)
		}
		_ = peer.SetReadDeadline(time.Now().Add(time.Second))
		got := make([]byte, len(payload))
		if _, err = io.ReadFull(peer, got); err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, payload) {
			t.Fatalf("outbound: %v", got)
		}
		if _, err = peer.Write(payload); err != nil {
			t.Fatal(err)
		}
		done := make(chan error, 1)
		go func() { _, err := io.ReadFull(p, got); done <- err }()
		if err = await(t, done); err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, payload) {
			t.Fatalf("inbound: %v", got)
		}
		go func() { _, err := p.Read(make([]byte, 1)); done <- err }()
		if err = p.Close(); err != nil {
			t.Fatal(err)
		}
		if err = await(t, done); err == nil {
			t.Fatal("closed read succeeded")
		}
		if _, err = p.Write([]byte("closed")); err == nil {
			t.Fatal("closed write succeeded")
		}
		if err = p.SetBreak(true); err == nil {
			t.Fatal("closed control succeeded")
		}
		if err = p.Close(); err != nil {
			t.Fatal(err)
		}
	}
}

func TestPTYCancelAndPeerLoss(t *testing.T) {
	for _, cancelPort := range []bool{true, false} {
		t.Run(map[bool]string{true: "cancel", false: "peer-loss"}[cancelPort], func(t *testing.T) {
			name, _, c := pair(t)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			p, err := Open(ctx, Options{PortName: name})
			if err != nil {
				t.Fatal(err)
			}
			defer p.Close()
			done := make(chan error, 1)
			go func() { _, e := p.Read(make([]byte, 1)); done <- e }()
			if cancelPort {
				cancel()
			} else {
				_ = c.Process.Kill()
			}
			if err = await(t, done); err == nil {
				t.Fatal("read did not report failure")
			}
			if cancelPort && !errors.Is(err, context.Canceled) {
				t.Fatalf("cancellation: %v", err)
			}
		})
	}
}

func TestPTYSignalsAndFlowControl(t *testing.T) {
	name, _, _ := pair(t)
	p, err := Open(context.Background(), Options{PortName: name, FlowControl: "RtsCts"})
	if err != nil {
		t.Fatal(err)
	}
	if err = p.SetRTS(true); !errors.Is(err, ErrRTSConflict) {
		t.Fatalf("RTS conflict: %v", err)
	}
	if err = p.SetDTR(true); !errors.Is(err, unix.ENOTTY) {
		t.Fatalf("PTY must report unsupported DTR: %v", err)
	}
	for _, on := range []bool{true, false} {
		if err = p.SetBreak(on); err != nil {
			t.Fatal(err)
		}
	}
	if err = p.Close(); err != nil {
		t.Fatal(err)
	}
	// PTYs cannot prove physical CTS/RTS behavior; only assert the actual termios bits.
	f, err := os.OpenFile(name, os.O_RDWR|unix.O_NOCTTY, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	s, err := unix.IoctlGetTermios(int(f.Fd()), unix.TCGETS)
	if err != nil {
		t.Fatal(err)
	}
	if s.Cflag&unix.CRTSCTS == 0 || s.Iflag&(unix.IXON|unix.IXOFF) != 0 {
		t.Fatalf("hardware flags: %+v", s)
	}
}

func TestPTYExplicitPinsFailWithoutSilentFallback(t *testing.T) {
	for _, dtr := range []bool{true, false} {
		for _, on := range []bool{true, false} {
			t.Run(fmt.Sprintf("DTR=%t/value=%t", dtr, on), func(t *testing.T) {
				name, _, _ := pair(t)
				o := Options{PortName: name}
				if dtr {
					o.DtrEnable = &on
				} else {
					o.RtsEnable = &on
				}
				p, err := Open(context.Background(), o)
				if p != nil {
					_ = p.Close()
					t.Fatal("unsupported PTY pin must not be silently ignored")
				}
				if !errors.Is(err, unix.ENOTTY) {
					t.Fatalf("unsupported pin error: %v", err)
				}
				// Failed setup must release its descriptor and exclusive access.
				p, err = Open(context.Background(), Options{PortName: name})
				if err != nil {
					t.Fatal(err)
				}
				defer p.Close()
				if err = p.SetRTS(on); !errors.Is(err, unix.ENOTTY) {
					t.Fatalf("manual RTS: %v", err)
				}
			})
		}
	}
}

func TestPTYParametersAndUnsupportedStopBits(t *testing.T) {
	name, _, _ := pair(t)
	f, err := os.OpenFile(name, os.O_RDWR|unix.O_NOCTTY, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	for _, baud := range []int{115200, 12345, 9600} {
		p, err := Open(context.Background(), Options{PortName: name, BaudRate: baud, DataBits: 8, Parity: "None", StopBits: "Two", FlowControl: "None"})
		if err != nil {
			t.Fatal(err)
		}
		s, err := unix.IoctlGetTermios(int(f.Fd()), unix.TCGETS2)
		if err != nil {
			_ = p.Close()
			t.Fatal(err)
		}
		if int(s.Ospeed) != baud || s.Cflag&unix.CSTOPB == 0 || s.Cflag&unix.CSIZE != unix.CS8 || s.Cflag&unix.PARENB != 0 {
			_ = p.Close()
			t.Fatalf("requested baud=%d, termios=%+v", baud, s)
		}
		if err = p.Close(); err != nil {
			t.Fatal(err)
		}
	}
	if p, err := Open(context.Background(), Options{PortName: name, StopBits: "OnePointFive"}); err == nil || p != nil {
		if p != nil {
			_ = p.Close()
		}
		t.Fatalf("Linux must reject unsupported 1.5 stop bits: %v", err)
	}
}

func TestPTYXonXoffAndBlockedWriteClose(t *testing.T) {
	for _, cancelPort := range []bool{false, true} {
		t.Run(map[bool]string{false: "close", true: "cancel"}[cancelPort], func(t *testing.T) {
			name, peer, _ := pair(t)
			// Open an observer before the serial owner takes TIOCEXCL.
			f, err := os.OpenFile(name, os.O_RDWR|unix.O_NOCTTY, 0)
			if err != nil {
				t.Fatal(err)
			}
			defer f.Close()
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			p, err := Open(ctx, Options{PortName: name, FlowControl: "XonXoff"})
			if err != nil {
				t.Fatal(err)
			}
			defer p.Close()
			s, err := unix.IoctlGetTermios(int(f.Fd()), unix.TCGETS)
			_ = f.Close()
			if err != nil {
				t.Fatal(err)
			}
			if s.Iflag&(unix.IXON|unix.IXOFF) != (unix.IXON|unix.IXOFF) || s.Iflag&unix.IXANY != 0 || s.Cc[unix.VSTART] != 17 || s.Cc[unix.VSTOP] != 19 {
				t.Fatalf("software flags: %+v", s)
			}
			if _, err = peer.Write([]byte{19}); err != nil {
				t.Fatal(err)
			}
			time.Sleep(50 * time.Millisecond)
			done := make(chan error, 1)
			go func() { _, e := p.Write([]byte("paused")); done <- e }()
			select {
			case err := <-done:
				t.Fatalf("XOFF did not block: %v", err)
			case <-time.After(100 * time.Millisecond):
			}
			if _, err = peer.Write([]byte{17}); err != nil {
				t.Fatal(err)
			}
			if err = await(t, done); err != nil {
				t.Fatal(err)
			}
			_ = peer.SetReadDeadline(time.Now().Add(time.Second))
			got := make([]byte, 6)
			if _, err = io.ReadFull(peer, got); err != nil || string(got) != "paused" {
				t.Fatalf("XON resume: %q %v", got, err)
			}
			_, _ = peer.Write([]byte{19})
			time.Sleep(50 * time.Millisecond)
			go func() { _, e := p.Write(bytes.Repeat([]byte("x"), 1<<20)); done <- e }()
			select {
			case err := <-done:
				t.Fatalf("expected blocked write: %v", err)
			case <-time.After(100 * time.Millisecond):
			}
			if cancelPort {
				cancel()
			} else if err = p.Close(); err != nil {
				t.Fatal(err)
			}
			if err = await(t, done); err == nil {
				t.Fatal("blocked write must fail on close")
			}
		})
	}
}
