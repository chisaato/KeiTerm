// Package serial implements device byte streams, independent of IPC and SSH.
package serial

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	native "github.com/keiterm/transport-engine/internal/serialnative"
)

var (
	ErrInvalidOptions = errors.New("invalid serial options")
	ErrRTSConflict    = errors.New("automatic RTS/CTS owns RTS; manual RTS is not allowed")
	// ErrWriteTimeout terminates this Port only. It also matches
	// os.ErrDeadlineExceeded and implements Timeout() bool.
	ErrWriteTimeout = &writeTimeoutError{}
)

type writeTimeoutError struct{}

func (*writeTimeoutError) Error() string   { return "serial write budget exceeded; port closed" }
func (*writeTimeoutError) Unwrap() error   { return os.ErrDeadlineExceeded }
func (*writeTimeoutError) Timeout() bool   { return true }
func (*writeTimeoutError) Temporary() bool { return false }

// Options uses the exact enum strings from Core.Models.SerialOptions.
// Zero BaudRate/DataBits and empty enums mean 9600/8/None/One/None.
// Nil signal pointers preserve driver defaults, without modem-pin ioctls.
type Options struct {
	PortName    string `json:"portName"`
	BaudRate    int    `json:"baudRate"`
	DataBits    int    `json:"dataBits"`
	Parity      string `json:"parity"`
	StopBits    string `json:"stopBits"`
	FlowControl string `json:"flowControl"`
	DtrEnable   *bool  `json:"dtrEnable,omitempty"`
	RtsEnable   *bool  `json:"rtsEnable,omitempty"`
	// Read timeout is a polling interval, never an idle-disconnect timeout.
	ReadTimeoutMilliseconds int `json:"readTimeoutMilliseconds"`
	// A write exceeding this budget closes the entire Port, not shared IPC.
	WriteTimeoutMilliseconds int `json:"writeTimeoutMilliseconds"`
}

// UnmarshalJSON distinguishes omitted defaults from explicitly invalid zero
// or null values. Direct Go zero-value Options use 100/500 ms defaults.
func (o *Options) UnmarshalJSON(data []byte) error {
	type plain Options
	v := plain{ReadTimeoutMilliseconds: 100, WriteTimeoutMilliseconds: 500}
	if err := json.Unmarshal(data, &v); err != nil {
		return err
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		return err
	}
	for key, value := range fields {
		if (strings.EqualFold(key, "readTimeoutMilliseconds") || strings.EqualFold(key, "writeTimeoutMilliseconds")) && strings.TrimSpace(string(value)) == "null" {
			return fmt.Errorf("%w: %s must be 1..5000", ErrInvalidOptions, key)
		}
	}
	if v.ReadTimeoutMilliseconds < 1 || v.ReadTimeoutMilliseconds > 5000 || v.WriteTimeoutMilliseconds < 1 || v.WriteTimeoutMilliseconds > 5000 {
		return fmt.Errorf("%w: timeouts must be 1..5000", ErrInvalidOptions)
	}
	*o = Options(v)
	return nil
}

// Port owns one open device. Context cancellation closes it, including pending
// reads and writes. Reads/writes serialize per direction, controls serialize
// with Close. This is not a network connection and has no resize/file API.
type Port struct {
	device       native.Port
	ctx          context.Context
	flow         string
	mu           sync.Mutex
	readMu       sync.Mutex
	writeMu      sync.Mutex
	closed       bool
	closeErr     error
	stop         func() bool
	closeCause   error
	writeTimeout time.Duration
}

var _ io.ReadWriteCloser = (*Port)(nil)

// Open opens the named local device. The context owns the entire Port lifetime,
// not only opening. Unsupported driver features return their native error.
func Open(ctx context.Context, options Options) (*Port, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if options.ReadTimeoutMilliseconds == 0 {
		options.ReadTimeoutMilliseconds = 100
	}
	if options.WriteTimeoutMilliseconds == 0 {
		options.WriteTimeoutMilliseconds = 500
	}
	if options.ReadTimeoutMilliseconds < 1 || options.ReadTimeoutMilliseconds > 5000 || options.WriteTimeoutMilliseconds < 1 || options.WriteTimeoutMilliseconds > 5000 {
		return nil, fmt.Errorf("%w: timeouts must be 1..5000", ErrInvalidOptions)
	}
	mode, err := options.mode()
	if err != nil {
		return nil, err
	}
	d, err := native.Open(options.PortName, mode)
	if err != nil {
		return nil, fmt.Errorf("open serial %q: %w", options.PortName, err)
	}
	if err = d.SetReadTimeout(time.Duration(options.ReadTimeoutMilliseconds) * time.Millisecond); err != nil {
		_ = d.Close()
		return nil, err
	}
	p := &Port{device: d, ctx: ctx, flow: mode.FlowControl, writeTimeout: time.Duration(options.WriteTimeoutMilliseconds) * time.Millisecond}
	// Register under mu: an already-canceled callback cannot race assigning stop.
	p.mu.Lock()
	p.stop = context.AfterFunc(ctx, func() { _ = p.closeWithCause(ctx.Err()) })
	p.mu.Unlock()
	if err = ctx.Err(); err != nil {
		_ = p.Close()
		return nil, err
	}
	return p, nil
}

func (o Options) mode() (*native.Mode, error) {
	bad := func(field string) (*native.Mode, error) { return nil, fmt.Errorf("%w: %s", ErrInvalidOptions, field) }
	if strings.TrimSpace(o.PortName) == "" || strings.ContainsRune(o.PortName, 0) {
		return bad("PortName")
	}
	if o.BaudRate == 0 {
		o.BaudRate = 9600
	}
	if o.DataBits == 0 {
		o.DataBits = 8
	}
	if o.BaudRate < 1 || uint64(o.BaudRate) > uint64(^uint32(0)) {
		return bad("BaudRate")
	}
	if o.DataBits < 5 || o.DataBits > 8 {
		return bad("DataBits")
	}
	parity, ok := map[string]native.Parity{"": native.NoParity, "None": native.NoParity, "Odd": native.OddParity, "Even": native.EvenParity, "Mark": native.MarkParity, "Space": native.SpaceParity}[o.Parity]
	if !ok {
		return bad("Parity")
	}
	stop, ok := map[string]native.StopBits{"": native.OneStopBit, "One": native.OneStopBit, "Two": native.TwoStopBits, "OnePointFive": native.OnePointFiveStopBits}[o.StopBits]
	if !ok {
		return bad("StopBits")
	}
	if o.FlowControl == "" {
		o.FlowControl = "None"
	}
	if o.FlowControl != "None" && o.FlowControl != "RtsCts" && o.FlowControl != "XonXoff" {
		return bad("FlowControl")
	}
	if o.FlowControl == "RtsCts" && o.RtsEnable != nil {
		return nil, errors.Join(ErrInvalidOptions, ErrRTSConflict)
	}
	m := &native.Mode{BaudRate: o.BaudRate, DataBits: o.DataBits, Parity: parity, StopBits: stop, FlowControl: o.FlowControl}
	if o.DtrEnable != nil || o.RtsEnable != nil {
		m.InitialStatusBits = &native.ModemOutputBits{DTR: o.DtrEnable, RTS: o.RtsEnable}
	}
	return m, nil
}

func (p *Port) ioError(err error) error {
	if err == nil {
		return nil
	}
	p.mu.Lock()
	cause := p.closeCause
	p.mu.Unlock()
	if cause != nil {
		return cause
	}
	if e := p.ctx.Err(); e != nil {
		return e
	}
	var pe *native.PortError
	if errors.As(err, &pe) && pe.Code() == native.PortClosed {
		return errors.Join(io.ErrClosedPipe, err)
	}
	return err
}

func (p *Port) Read(b []byte) (int, error) {
	p.readMu.Lock()
	defer p.readMu.Unlock()
	if len(b) == 0 {
		return 0, nil
	}
	for {
		n, err := p.device.Read(b)
		if n == 0 && err == nil {
			continue
		}
		return n, p.ioError(err)
	}
}

func (p *Port) Write(b []byte) (int, error) {
	p.writeMu.Lock()
	defer p.writeMu.Unlock()
	p.mu.Lock()
	if p.closed {
		err := p.closeCause
		p.mu.Unlock()
		return 0, err
	}
	if err := p.ctx.Err(); err != nil {
		p.mu.Unlock()
		return 0, err
	}
	// Pending -> completed or timed out has one atomic winner. In particular,
	// a callback already scheduled cannot close a later write's device after
	// this operation completed. Join a running callback before returning.
	var state atomic.Int32
	timerDone := make(chan struct{})
	timer := time.AfterFunc(p.writeTimeout, func() {
		defer close(timerDone)
		if !state.CompareAndSwap(0, 2) {
			return
		}
		_ = p.closeWithCause(ErrWriteTimeout)
	})
	p.mu.Unlock()
	n, err := p.device.Write(b)
	state.CompareAndSwap(0, 1)
	if !timer.Stop() {
		<-timerDone
	}
	if state.Load() == 2 {
		return n, p.ioError(io.ErrClosedPipe)
	}
	if err == nil && n < len(b) {
		err = io.ErrShortWrite
	}
	return n, p.ioError(err)
}

func (p *Port) Close() error {
	return p.closeWithCause(io.ErrClosedPipe)
}

func (p *Port) closeWithCause(cause error) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if !p.closed {
		// Context cancellation already observed at the close linearization
		// point takes precedence over an independently expiring write timer.
		if err := p.ctx.Err(); err != nil {
			cause = err
		}
		p.closed = true
		p.closeCause = cause
		if p.stop != nil {
			p.stop()
		}
		p.closeErr = p.device.Close()
	}
	return p.closeErr
}

func (p *Port) control(action func() error) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed {
		return p.closeCause
	}
	if err := p.ctx.Err(); err != nil {
		return err
	}
	return action()
}

func (p *Port) SetDTR(on bool) error { return p.control(func() error { return p.device.SetDTR(on) }) }
func (p *Port) SetRTS(on bool) error {
	return p.control(func() error {
		if p.flow == "RtsCts" {
			return ErrRTSConflict
		}
		return p.device.SetRTS(on)
	})
}

// SetBreak asserts or clears break; it is not a timed Break(duration) emulation.
func (p *Port) SetBreak(on bool) error {
	return p.control(func() error { return p.device.SetBreakState(on) })
}
