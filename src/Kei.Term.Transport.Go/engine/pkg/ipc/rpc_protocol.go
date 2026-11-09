package ipc

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"sync"

	"github.com/keiterm/transport-engine/pkg/serial"
	"github.com/keiterm/transport-engine/pkg/telnet"
)

// Non-SSH terminals do not implement Session or expose a fake SFTP capability.
type protocolSession struct {
	port   io.ReadWriteCloser
	stream *Stream
	once   sync.Once
}

func (s *protocolSession) finish(err error) {
	s.once.Do(func() { _ = s.port.Close(); finishStream(s.stream, err) })
}
func (p *peer) protocolStream(id uint32) *protocolSession {
	for _, s := range p.protocols {
		if s.stream.id == id {
			return s
		}
	}
	return nil
}

func (p *peer) retireProtocols() {
	for id, s := range p.protocols {
		s.stream.mu.Lock()
		closed := s.stream.closed
		s.stream.mu.Unlock()
		if closed {
			delete(p.protocols, id)
		}
	}
}

func (r *Router) callProtocol(p *peer, req Request) (bool, any, func(), error) {
	if !strings.HasPrefix(req.Method, "protocol.") && !strings.HasPrefix(req.Method, "telnet.") && !strings.HasPrefix(req.Method, "serial.") {
		return false, nil, nil, nil
	}
	if len(req.ID) == 0 {
		return true, nil, nil, nil
	}
	if req.Method == "serial.listPorts" {
		names, err := serial.ListPorts(p.ctx)
		return true, map[string]any{"names": names}, nil, err
	}
	if p.protocols == nil {
		p.protocols = make(map[string]*protocolSession)
	}
	if req.Method == "telnet.connect" || req.Method == "serial.connect" {
		p.retireProtocols()
		for id, s := range p.streams {
			s.mu.Lock()
			closed := s.closed
			s.mu.Unlock()
			if closed {
				delete(p.streams, id)
				delete(p.resize, id)
			}
		}
		if len(p.streams) >= 256 || p.next == ^uint32(0) || len(p.sessions)+len(p.protocols) >= 64 {
			return true, nil, nil, &RPCError{-32002, "IPC resource limit exceeded"}
		}
		var port io.ReadWriteCloser
		var err error
		if req.Method == "telnet.connect" {
			var options telnet.Options
			if json.Unmarshal(req.Params, &options) != nil {
				return true, nil, nil, &RPCError{-32602, "Invalid Telnet params"}
			}
			port, err = telnet.Dial(p.ctx, options)
		} else {
			port, err = openSerial(p.ctx, req.Params)
		}
		if err != nil {
			return true, nil, nil, err
		}
		stream := p.stream()
		id := fmt.Sprintf("protocol-%d", stream.id)
		session := &protocolSession{port: port, stream: stream}
		p.protocols[id] = session
		after := func() {
			p.run(func() {
				_, err := io.Copy(port, stream)
				if errors.Is(err, io.EOF) {
					err = nil
				}
				session.finish(err)
			})
			p.run(func() {
				_, err := io.Copy(stream, port)
				if errors.Is(err, io.EOF) {
					err = nil
				}
				session.finish(err)
			})
		}
		return true, map[string]any{"sessionId": id, "streamId": stream.id}, after, nil
	}
	var args struct {
		SessionID string `json:"sessionId"`
		Cols      int    `json:"cols"`
		Rows      int    `json:"rows"`
		Enabled   bool   `json:"enabled"`
	}
	if json.Unmarshal(req.Params, &args) != nil || args.SessionID == "" {
		return true, nil, nil, &RPCError{-32602, "Invalid protocol params"}
	}
	s := p.protocols[args.SessionID]
	if s == nil {
		return true, nil, nil, &RPCError{-32001, "Unknown protocol session"}
	}
	switch req.Method {
	case "protocol.disconnect":
		return true, true, func() { s.finish(nil) }, nil
	case "protocol.resize":
		resizer, ok := s.port.(interface{ Resize(int, int) error })
		if !ok {
			return true, nil, nil, &RPCError{-32003, "Terminal does not support resize"}
		}
		return true, true, nil, resizer.Resize(args.Cols, args.Rows)
	case "serial.setDtr", "serial.setRts", "serial.setBreak":
		return true, true, nil, controlSerial(s.port, req.Method, args.Enabled)
	default:
		return true, nil, nil, &RPCError{-32601, "Method not found"}
	}
}
