// Package ipc implements the desktop sidecar wire protocol. Integers are big endian;
// stream zero is reserved for JSON-RPC. A Close frame ends the sender's data.
package ipc

import (
	"bytes"
	"encoding/binary"
	"errors"
	"io"
	"sync"
)

type FrameType byte

const (
	Data FrameType = iota + 1
	WindowResize
	Close
	Error
	JSONRPC
	MaxPayload = 4 << 20
)

type Frame struct {
	StreamID uint32
	Type     FrameType
	Payload  []byte
}

// Codec serializes complete frames, including short underlying reads/writes.
type Codec struct {
	r        io.Reader
	w        io.Writer
	rmu, wmu sync.Mutex
}

func NewCodec(r io.Reader, w io.Writer) *Codec { return &Codec{r: r, w: w} }
func (c *Codec) ReadFrame() (Frame, error) {
	c.rmu.Lock()
	defer c.rmu.Unlock()
	var h [9]byte
	if _, err := io.ReadFull(c.r, h[:]); err != nil {
		return Frame{}, err
	}
	f := Frame{StreamID: binary.BigEndian.Uint32(h[:4]), Type: FrameType(h[4])}
	n := binary.BigEndian.Uint32(h[5:])
	if n > MaxPayload || f.Type < Data || f.Type > JSONRPC {
		return Frame{}, errors.New("invalid frame header")
	}
	f.Payload = make([]byte, int(n))
	_, err := io.ReadFull(c.r, f.Payload)
	return f, err
}
func (c *Codec) WriteFrame(f Frame) error {
	if len(f.Payload) > MaxPayload || f.Type < Data || f.Type > JSONRPC {
		return errors.New("invalid frame")
	}
	c.wmu.Lock()
	defer c.wmu.Unlock()
	var h [9]byte
	binary.BigEndian.PutUint32(h[:4], f.StreamID)
	h[4] = byte(f.Type)
	binary.BigEndian.PutUint32(h[5:], uint32(len(f.Payload)))
	for _, p := range [][]byte{h[:], f.Payload} {
		for len(p) > 0 {
			n, err := c.w.Write(p)
			if err != nil {
				return err
			}
			if n == 0 {
				return io.ErrShortWrite
			}
			p = p[n:]
		}
	}
	return nil
}

// Stream bridges a stream ID to io.ReadWriteCloser. Receive is nonblocking and
// bounded so an unread stream cannot stall unrelated RPC or terminal traffic.
type Stream struct {
	id            uint32
	codec         *Codec
	mu            sync.Mutex
	cond          *sync.Cond
	buffer        bytes.Buffer
	ended, closed bool
	err           error
	writeMu       sync.Mutex
}

func NewStream(id uint32, codec *Codec) *Stream {
	s := &Stream{id: id, codec: codec}
	s.cond = sync.NewCond(&s.mu)
	return s
}
func (s *Stream) Receive(f Frame) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.ended || s.closed {
		return io.ErrClosedPipe
	}
	switch f.Type {
	case Data:
		if s.buffer.Len()+len(f.Payload) > 4*MaxPayload {
			s.err, s.ended = errors.New("stream receive buffer exceeded"), true
		} else {
			_, _ = s.buffer.Write(f.Payload)
		}
	case Close:
		s.ended = true
	case Error:
		s.err, s.ended = errors.New(string(f.Payload)), true
	default:
		return errors.New("unexpected stream frame")
	}
	s.cond.Broadcast()
	return s.err
}
func (s *Stream) Read(p []byte) (int, error) {
	if len(p) == 0 {
		return 0, nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for s.buffer.Len() == 0 && !s.ended && !s.closed {
		s.cond.Wait()
	}
	if s.buffer.Len() > 0 {
		return s.buffer.Read(p)
	}
	if s.err != nil {
		return 0, s.err
	}
	return 0, io.EOF
}
func (s *Stream) Write(p []byte) (int, error) {
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	s.mu.Lock()
	closed := s.closed
	s.mu.Unlock()
	if closed {
		return 0, io.ErrClosedPipe
	}
	n := 0
	for len(p) > 0 {
		size := min(len(p), 32<<10)
		if err := s.codec.WriteFrame(Frame{s.id, Data, p[:size]}); err != nil {
			return n, err
		}
		n += size
		p = p[size:]
	}
	return n, nil
}
func (s *Stream) abort(err error) {
	s.mu.Lock()
	s.closed, s.ended, s.err = true, true, err
	s.cond.Broadcast()
	s.mu.Unlock()
}
func (s *Stream) Close() error {
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return nil
	}
	s.closed = true
	s.cond.Broadcast()
	s.mu.Unlock()
	return s.codec.WriteFrame(Frame{StreamID: s.id, Type: Close})
}
