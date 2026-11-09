package serial

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"testing"
	"time"
)

func TestTimeoutOptionsJSON(t *testing.T) {
	for _, text := range []string{`{"readTimeoutMilliseconds":0}`, `{"writeTimeoutMilliseconds":5001}`, `{"readTimeoutMilliseconds":-1}`, `{"writeTimeoutMilliseconds":null}`} {
		var o Options
		if err := json.Unmarshal([]byte(text), &o); !errors.Is(err, ErrInvalidOptions) {
			t.Fatalf("%s: %v", text, err)
		}
	}
	var o Options
	if err := json.Unmarshal([]byte(`{"portName":"x","readTimeoutMilliseconds":7,"writeTimeoutMilliseconds":19}`), &o); err != nil {
		t.Fatal(err)
	}
	if o.ReadTimeoutMilliseconds != 7 || o.WriteTimeoutMilliseconds != 19 {
		t.Fatalf("timeouts ignored: %+v", o)
	}
	for _, o := range []Options{{PortName: "x", ReadTimeoutMilliseconds: -1}, {PortName: "x", WriteTimeoutMilliseconds: 5001}} {
		if _, err := Open(context.Background(), o); !errors.Is(err, ErrInvalidOptions) {
			t.Fatalf("invalid timeout: %v", err)
		}
	}
}

func TestPTYReadTimeoutPollsWithoutEOF(t *testing.T) {
	name, peer, _ := pair(t)
	p, err := Open(context.Background(), Options{PortName: name, ReadTimeoutMilliseconds: 5})
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close()
	done := make(chan error, 1)
	go func() {
		b := make([]byte, 1)
		_, e := io.ReadFull(p, b)
		if e == nil && b[0] != 'z' {
			e = errors.New("wrong payload")
		}
		done <- e
	}()
	select {
	case e := <-done:
		t.Fatalf("idle read must continue: %v", e)
	case <-time.After(80 * time.Millisecond):
	}
	_, _ = peer.Write([]byte("z"))
	if err = await(t, done); err != nil {
		t.Fatal(err)
	}
}

func TestPTYWriteBudgetAndErrorOwnership(t *testing.T) {
	for _, mode := range []string{"timeout", "default-timeout", "close", "cancel", "deadline", "close-then-cancel"} {
		t.Run(mode, func(t *testing.T) {
			name, peer, _ := pair(t)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			if mode == "deadline" {
				ctx, cancel = context.WithTimeout(ctx, 140*time.Millisecond)
				defer cancel()
			}
			budget := 200
			if mode == "default-timeout" {
				budget = 0
			}
			p, err := Open(ctx, Options{PortName: name, FlowControl: "XonXoff", WriteTimeoutMilliseconds: budget})
			if err != nil {
				t.Fatal(err)
			}
			defer p.Close()
			_, _ = peer.Write([]byte{19})
			time.Sleep(30 * time.Millisecond)
			done := make(chan error, 1)
			start := time.Now()
			go func() { _, e := p.Write([]byte("blocked")); done <- e }()
			switch mode {
			case "close":
				time.Sleep(20 * time.Millisecond)
				_ = p.Close()
			case "cancel":
				time.Sleep(20 * time.Millisecond)
				cancel()
			case "close-then-cancel":
				time.Sleep(20 * time.Millisecond)
				_ = p.Close()
				cancel()
			}
			err = await(t, done)
			switch mode {
			case "timeout", "default-timeout":
				if !errors.Is(err, ErrWriteTimeout) || !errors.Is(err, os.ErrDeadlineExceeded) {
					t.Fatalf("timeout identity: %v", err)
				}
				minimum := 150 * time.Millisecond
				if mode == "default-timeout" {
					minimum = 450 * time.Millisecond
				}
				if time.Since(start) < minimum || time.Since(start) > 2*time.Second {
					t.Fatalf("write budget: %v", time.Since(start))
				}
			case "close", "close-then-cancel":
				if !errors.Is(err, io.ErrClosedPipe) || errors.Is(err, ErrWriteTimeout) {
					t.Fatalf("close identity: %v", err)
				}
			case "cancel":
				if !errors.Is(err, context.Canceled) || errors.Is(err, ErrWriteTimeout) {
					t.Fatalf("cancel identity: %v", err)
				}
			case "deadline":
				if !errors.Is(err, context.DeadlineExceeded) || errors.Is(err, ErrWriteTimeout) {
					t.Fatalf("deadline identity: %v", err)
				}
			}
			if mode == "timeout" || mode == "default-timeout" {
				if _, e := p.Read(make([]byte, 1)); !errors.Is(e, ErrWriteTimeout) {
					t.Fatalf("timeout must terminate port/read: %v", e)
				}
			}
		})
	}
}

func TestPTYCompletedWriteDisarmsTimer(t *testing.T) {
	name, peer, _ := pair(t)
	p, err := Open(context.Background(), Options{PortName: name, WriteTimeoutMilliseconds: 20})
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close()
	for i := 0; i < 5; i++ {
		if _, err = p.Write([]byte("a")); err != nil {
			t.Fatal(err)
		}
		_ = peer.SetReadDeadline(time.Now().Add(time.Second))
		b := make([]byte, 1)
		if _, err = io.ReadFull(peer, b); err != nil {
			t.Fatal(err)
		}
		time.Sleep(40 * time.Millisecond)
	}
}

func TestPTYCloseRacesWriteTimeout(t *testing.T) {
	for i := 0; i < 15; i++ {
		name, peer, _ := pair(t)
		p, err := Open(context.Background(), Options{PortName: name, FlowControl: "XonXoff", WriteTimeoutMilliseconds: 15})
		if err != nil {
			t.Fatal(err)
		}
		_, _ = peer.Write([]byte{19})
		time.Sleep(10 * time.Millisecond)
		done := make(chan error, 1)
		go func() { _, e := p.Write([]byte("race")); done <- e }()
		time.Sleep(15 * time.Millisecond)
		closed := make(chan error, 1)
		go func() { closed <- p.Close() }()
		err = await(t, done)
		if !errors.Is(err, io.ErrClosedPipe) && !errors.Is(err, ErrWriteTimeout) {
			t.Fatalf("race identity: %v", err)
		}
		if e := await(t, closed); e != nil {
			t.Fatal(e)
		}
		_, again := p.Write([]byte("after"))
		if errors.Is(err, ErrWriteTimeout) != errors.Is(again, ErrWriteTimeout) {
			t.Fatalf("terminal owner changed: %v -> %v", err, again)
		}
	}
}
