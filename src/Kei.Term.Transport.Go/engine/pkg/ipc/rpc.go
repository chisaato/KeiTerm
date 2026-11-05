package ipc

import (
	"context"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"strconv"
	"sync"
	"time"

	"github.com/keiterm/transport-engine/pkg/transport"
	"golang.org/x/crypto/ssh"
)

type Request struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params,omitempty"`
}
type RPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}
type Response struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Result  any             `json:"result,omitempty"`
	Error   *RPCError       `json:"error,omitempty"`
}
type Auth struct {
	Password   *string `json:"password,omitempty"`
	PrivateKey string  `json:"privateKey,omitempty"`
	Passphrase string  `json:"passphrase,omitempty"`
}
type Endpoint struct {
	Host string `json:"host"`
	Port int    `json:"port"`
	User string `json:"user"`
	Auth Auth   `json:"auth"`
	// The desktop must explicitly approve a SHA256 host key fingerprint first.
	HostKeySHA256         string `json:"hostKeySha256"`
	InsecureIgnoreHostKey bool   `json:"insecureIgnoreHostKey"`
}
type ConnectParams struct {
	Endpoint
	JumpHosts        []Endpoint        `json:"jumpHosts"`
	TerminalType     string            `json:"terminalType"`
	Cols             int               `json:"cols"`
	Rows             int               `json:"rows"`
	KeepAliveSeconds int               `json:"keepAliveSeconds"`
	Env              map[string]string `json:"env"`
	StartupScript    string            `json:"startupScript"`
}
type Shell interface {
	io.ReadWriteCloser
	Stderr() io.Reader
	Resize(int, int) error
}
type FileSystem interface {
	ListDir(string) ([]os.FileInfo, error)
	Stat(string) (os.FileInfo, error)
	Read(string) (io.ReadCloser, error)
	Write(string) (io.WriteCloser, error)
	Mkdir(string) error
	Remove(string) error
	Rename(string, string) error
	Close() error
}
type Session interface {
	Close() error
	OpenShell(transport.PTYOptions) (Shell, error)
	OpenSFTP() (FileSystem, error)
}
type sshSession struct{ *transport.Session }

func (s sshSession) OpenShell(o transport.PTYOptions) (Shell, error) { return s.Session.OpenShell(o) }
func (s sshSession) OpenSFTP() (FileSystem, error)                   { return s.Session.OpenSFTP() }

type Connector func(context.Context, ConnectParams) (Session, error)

func connectSSH(ctx context.Context, p ConnectParams) (Session, error) {
	return dialSSH(ctx, p, nil)
}

type presentedKey struct {
	KeyType         string `json:"keyType"`
	PublicKeyBase64 string `json:"publicKeyBase64"`
	Fingerprint     string `json:"hostKeySha256"`
}

var errKeyProbed = errors.New("host key probe complete")

func dialSSH(ctx context.Context, p ConnectParams, probe *presentedKey) (Session, error) {
	convert := func(e Endpoint) (transport.Endpoint, error) {
		if e.Port == 0 {
			e.Port = 22
		}
		if e.Host == "" || e.User == "" || e.Port < 1 || e.Port > 65535 || (e.HostKeySHA256 == "" && !e.InsecureIgnoreHostKey) {
			return transport.Endpoint{}, errors.New("host, user, valid port and approved hostKeySha256 required")
		}
		return transport.Endpoint{Address: net.JoinHostPort(e.Host, strconv.Itoa(e.Port)), User: e.User,
			Auth: transport.Auth{Password: e.Auth.Password, PrivateKey: []byte(e.Auth.PrivateKey), Passphrase: []byte(e.Auth.Passphrase)}, Timeout: 15 * time.Second,
			HostKeyCallback: func(_ string, _ net.Addr, key ssh.PublicKey) error {
				if e.HostKeySHA256 == "" && e.InsecureIgnoreHostKey {
					log.Printf("Explicitly accepting unpinned SSH host key for %s:%d: %s", e.Host, e.Port, ssh.FingerprintSHA256(key))
					return nil
				}
				if subtle.ConstantTimeCompare([]byte(ssh.FingerprintSHA256(key)), []byte(e.HostKeySHA256)) != 1 {
					return errors.New("SSH host key fingerprint mismatch")
				}
				return nil
			}}, nil
	}
	if probe != nil {
		// The probe stops at key exchange, before any target authentication.
		p.Endpoint.InsecureIgnoreHostKey = true
		p.Endpoint.HostKeySHA256 = ""
		password := ""
		p.Endpoint.Auth = Auth{Password: &password}
	}
	target, err := convert(p.Endpoint)
	if err != nil {
		return nil, err
	}
	if probe != nil {
		target.HostKeyCallback = func(_ string, _ net.Addr, key ssh.PublicKey) error {
			*probe = presentedKey{key.Type(), base64.StdEncoding.EncodeToString(key.Marshal()), ssh.FingerprintSHA256(key)}
			return errKeyProbed
		}
	}
	jumps := make([]transport.Endpoint, 0, len(p.JumpHosts))
	for _, hop := range p.JumpHosts {
		e, err := convert(hop)
		if err != nil {
			return nil, err
		}
		jumps = append(jumps, e)
	}
	s, err := (transport.Dialer{}).Dial(ctx, target, jumps...)
	if err != nil {
		return nil, err
	}
	s.StartKeepAlive(time.Duration(p.KeepAliveSeconds) * time.Second)
	return sshSession{s}, nil
}

type liveSession struct {
	session Session
	shell   Shell
	fs      FileSystem
	stream  *Stream
}
type FileInfo struct {
	Name  string `json:"name"`
	Size  int64  `json:"size"`
	Mode  uint32 `json:"mode"`
	IsDir bool   `json:"isDir"`
	Mtime int64  `json:"mtime"`
}

func fileInfo(f os.FileInfo) FileInfo {
	return FileInfo{f.Name(), f.Size(), uint32(f.Mode()), f.IsDir(), f.ModTime().Unix()}
}

// Router is scoped to one authenticated local IPC connection. Session IDs and
// stream IDs are never shared with other desktop connections.
type Router struct {
	Connect  Connector
	Shutdown func()
}
type peer struct {
	codec    *Codec
	sessions map[string]*liveSession
	streams  map[uint32]*Stream
	resize   map[uint32]Shell
	next     uint32
	wg       sync.WaitGroup
	ctx      context.Context
}

func (p *peer) stream() *Stream {
	p.next++
	s := NewStream(p.next, p.codec)
	p.streams[p.next] = s
	return s
}
func (p *peer) run(fn func()) { p.wg.Add(1); go func() { defer p.wg.Done(); fn() }() }

func (r *Router) Serve(ctx context.Context, conn net.Conn) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stop := context.AfterFunc(ctx, func() { _ = conn.Close() })
	defer stop()
	p := &peer{codec: NewCodec(conn, conn), sessions: make(map[string]*liveSession), streams: make(map[uint32]*Stream), resize: make(map[uint32]Shell), ctx: ctx}
	defer func() {
		cancel()
		_ = conn.Close()
		for _, s := range p.streams {
			s.abort(io.ErrClosedPipe)
		}
		for _, s := range p.sessions {
			_ = s.session.Close()
			_ = s.shell.Close()
			if s.fs != nil {
				_ = s.fs.Close()
			}
		}
		p.wg.Wait()
	}()
	for {
		f, err := p.codec.ReadFrame()
		if err != nil {
			return err
		}
		if f.Type == JSONRPC {
			if f.StreamID != 0 {
				return errors.New("JSON-RPC requires stream zero")
			}
			response, after := r.dispatch(p, f.Payload)
			if response != nil {
				data, err := json.Marshal(response)
				if err != nil {
					return err
				}
				if err := p.codec.WriteFrame(Frame{Type: JSONRPC, Payload: data}); err != nil {
					return err
				}
			}
			if after != nil {
				after()
			}
			continue
		}
		s := p.streams[f.StreamID]
		if s == nil {
			// A peer may acknowledge EOF after a completed stream was retired.
			if f.Type == Close && f.StreamID > 0 && f.StreamID <= p.next {
				continue
			}
			return errors.New("unknown stream")
		}
		if f.Type == WindowResize {
			var size struct {
				Cols int `json:"cols"`
				Rows int `json:"rows"`
			}
			shell := p.resize[f.StreamID]
			if err := json.Unmarshal(f.Payload, &size); err != nil || shell == nil || size.Cols <= 0 || size.Rows <= 0 {
				return errors.New("invalid resize")
			}
			if err := shell.Resize(size.Cols, size.Rows); err != nil {
				return err
			}
		} else if err := s.Receive(f); err != nil {
			return err
		}
	}
}

func (r *Router) dispatch(p *peer, data []byte) (*Response, func()) {
	var req Request
	if !json.Valid(data) {
		return &Response{JSONRPC: "2.0", ID: json.RawMessage("null"), Error: &RPCError{-32700, "Parse error"}}, nil
	}
	if err := json.Unmarshal(data, &req); err != nil {
		return &Response{JSONRPC: "2.0", ID: json.RawMessage("null"), Error: &RPCError{-32600, "Invalid Request"}}, nil
	}
	id := req.ID
	if len(id) == 0 {
		id = json.RawMessage("null")
	}
	res := &Response{JSONRPC: "2.0", ID: id}
	validID := true
	if len(req.ID) > 0 {
		var value any
		_ = json.Unmarshal(req.ID, &value)
		switch value.(type) {
		case string, float64, nil:
		default:
			validID = false
		}
	}
	if req.JSONRPC != "2.0" || req.Method == "" || !validID {
		res.ID = json.RawMessage("null")
		res.Error = &RPCError{-32600, "Invalid Request"}
		return res, nil
	}
	if len(req.ID) == 0 {
		// Notifications cannot allocate resources whose identifiers cannot be returned.
		if req.Method != "daemon.ping" && req.Method != "daemon.shutdown" && req.Method != "session.resize" && req.Method != "session.disconnect" {
			return nil, nil
		}
	}
	result, after, err := r.call(p, req)
	if len(req.ID) == 0 {
		return nil, after
	}
	if err != nil {
		var rpcErr *RPCError
		if errors.As(err, &rpcErr) {
			res.Error = rpcErr
		} else {
			res.Error = &RPCError{-32000, err.Error()}
		}
	} else {
		if result == nil {
			result = true
		}
		res.Result = result
	}
	return res, after
}
func (e *RPCError) Error() string { return e.Message }
func (r *Router) call(p *peer, req Request) (any, func(), error) {
	if req.Method == "session.connect" || req.Method == "sftp.upload_stream" || req.Method == "sftp.download_stream" {
		// Retire completed streams on the reader goroutine only. Keep live
		// allocations bounded, including peers that never finish uploads.
		for id, stream := range p.streams {
			stream.mu.Lock()
			closed := stream.closed
			stream.mu.Unlock()
			if closed {
				delete(p.streams, id)
				delete(p.resize, id)
			}
		}
		if len(p.streams) >= 256 || p.next == ^uint32(0) || (req.Method == "session.connect" && len(p.sessions) >= 64) {
			return nil, nil, &RPCError{-32002, "IPC resource limit exceeded"}
		}
	}
	if req.Method == "daemon.ping" {
		return map[string]any{"pong": true}, nil, nil
	}
	if req.Method == "daemon.shutdown" {
		return true, r.Shutdown, nil
	}
	if req.Method == "session.probeHostKey" {
		var args ConnectParams
		if json.Unmarshal(req.Params, &args) != nil {
			return nil, nil, &RPCError{-32602, "Invalid probe params"}
		}
		ctx, cancel := context.WithTimeout(p.ctx, 30*time.Second)
		defer cancel()
		var key presentedKey
		_, err := dialSSH(ctx, args, &key)
		if errors.Is(err, errKeyProbed) {
			return key, nil, nil
		}
		return nil, nil, err
	}
	if req.Method == "session.connect" {
		var args ConnectParams
		if json.Unmarshal(req.Params, &args) != nil || args.Cols <= 0 || args.Rows <= 0 {
			return nil, nil, &RPCError{-32602, "Invalid connect params"}
		}
		connector := r.Connect
		if connector == nil {
			connector = connectSSH
		}
		ctx, cancel := context.WithTimeout(p.ctx, 30*time.Second)
		defer cancel()
		session, err := connector(ctx, args)
		if err != nil {
			return nil, nil, err
		}
		stopOpening := context.AfterFunc(ctx, func() { _ = session.Close() })
		shell, err := session.OpenShell(transport.PTYOptions{Term: args.TerminalType, Columns: args.Cols, Rows: args.Rows, Env: args.Env, StartupScript: args.StartupScript})
		stopOpening()
		if ctx.Err() != nil {
			if shell != nil {
				_ = shell.Close()
			}
			_ = session.Close()
			return nil, nil, ctx.Err()
		}
		if err != nil {
			_ = session.Close()
			return nil, nil, err
		}
		stream := p.stream()
		id := fmt.Sprintf("session-%d", stream.id)
		p.sessions[id] = &liveSession{session: session, shell: shell, stream: stream}
		p.resize[stream.id] = shell
		after := func() {
			p.run(func() { _, _ = io.Copy(shell, stream); _ = shell.Close() })
			p.run(func() {
				var wg sync.WaitGroup
				wg.Add(1)
				go func() { defer wg.Done(); _, _ = io.Copy(stream, shell.Stderr()) }()
				_, _ = io.Copy(stream, shell)
				wg.Wait()
				_ = stream.Close()
			})
		}
		return map[string]any{"sessionId": id, "streamId": stream.id}, after, nil
	}
	var args struct {
		SessionID string `json:"sessionId"`
		Path      string `json:"path"`
		OldPath   string `json:"oldPath"`
		NewPath   string `json:"newPath"`
		IsDir     bool   `json:"isDir"`
		Cols      int    `json:"cols"`
		Rows      int    `json:"rows"`
	}
	switch req.Method {
	case "session.resize", "session.disconnect", "sftp.open", "sftp.list", "sftp.stat", "sftp.mkdir", "sftp.remove", "sftp.rename", "sftp.upload_stream", "sftp.download_stream":
	default:
		return nil, nil, &RPCError{-32601, "Method not found"}
	}
	if json.Unmarshal(req.Params, &args) != nil || args.SessionID == "" {
		return nil, nil, &RPCError{-32602, "Invalid params"}
	}
	s := p.sessions[args.SessionID]
	if s == nil {
		return nil, nil, &RPCError{-32001, "Unknown session"}
	}
	if req.Method == "session.resize" {
		if args.Cols <= 0 || args.Rows <= 0 {
			return nil, nil, &RPCError{-32602, "Invalid dimensions"}
		}
		return true, nil, s.shell.Resize(args.Cols, args.Rows)
	}
	if req.Method == "session.disconnect" {
		delete(p.sessions, args.SessionID)
		delete(p.resize, s.stream.id)
		s.stream.abort(io.EOF)
		err := s.session.Close()
		_ = s.shell.Close()
		if s.fs != nil {
			_ = s.fs.Close()
		}
		return true, func() { _ = p.codec.WriteFrame(Frame{StreamID: s.stream.id, Type: Close}) }, err
	}
	if s.fs == nil {
		fs, err := s.session.OpenSFTP()
		if err != nil {
			return nil, nil, err
		}
		s.fs = fs
	}
	fs := s.fs
	switch req.Method {
	case "sftp.open":
		return map[string]string{"sftpId": args.SessionID}, nil, nil
	case "sftp.list":
		items, err := fs.ListDir(args.Path)
		if err != nil {
			return nil, nil, err
		}
		result := make([]FileInfo, 0, len(items))
		for _, f := range items {
			result = append(result, fileInfo(f))
		}
		return result, nil, nil
	case "sftp.stat":
		f, err := fs.Stat(args.Path)
		if err != nil {
			return nil, nil, err
		}
		return fileInfo(f), nil, nil
	case "sftp.mkdir":
		return true, nil, fs.Mkdir(args.Path)
	case "sftp.remove":
		f, err := fs.Stat(args.Path)
		if err != nil {
			return nil, nil, err
		}
		if f.IsDir() != args.IsDir {
			return nil, nil, errors.New("isDir does not match remote entry")
		}
		return true, nil, fs.Remove(args.Path)
	case "sftp.rename":
		return true, nil, fs.Rename(args.OldPath, args.NewPath)
	case "sftp.upload_stream":
		file, err := fs.Write(args.Path)
		if err != nil {
			return nil, nil, err
		}
		stream := p.stream()
		return map[string]uint32{"streamId": stream.id}, func() {
			p.run(func() {
				_, err := io.Copy(file, stream)
				closeErr := file.Close()
				if err == nil {
					err = closeErr
				}
				finishStream(stream, err)
			})
		}, nil
	case "sftp.download_stream":
		file, err := fs.Read(args.Path)
		if err != nil {
			return nil, nil, err
		}
		stream := p.stream()
		return map[string]uint32{"streamId": stream.id}, func() {
			p.run(func() {
				_, err := io.Copy(stream, file)
				closeErr := file.Close()
				if err == nil {
					err = closeErr
				}
				finishStream(stream, err)
			})
		}, nil
	}
	return nil, nil, &RPCError{-32601, "Method not found"}
}
func finishStream(s *Stream, err error) {
	if err != nil {
		_ = s.codec.WriteFrame(Frame{StreamID: s.id, Type: Error, Payload: []byte(err.Error())})
	}
	_ = s.Close()
}
