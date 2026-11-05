package ipc

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"testing"
	"time"

	"github.com/keiterm/transport-engine/pkg/transport"
)

type shortWriter struct{ bytes.Buffer }

func (w *shortWriter) Write(p []byte) (int, error) { return w.Buffer.Write(p[:min(2, len(p))]) }

func TestFrameRoundTrip(t *testing.T) {
	for _, kind := range []FrameType{Data, WindowResize, Close, Error, JSONRPC} {
		w := &shortWriter{}
		want := Frame{0x12345678, kind, []byte{0, 1, 255, 0, 42}}
		codec := NewCodec(&w.Buffer, w)
		if err := codec.WriteFrame(want); err != nil {
			t.Fatal(err)
		}
		raw := w.Bytes()
		if len(raw) != 14 || binary.BigEndian.Uint32(raw[:4]) != want.StreamID || raw[4] != byte(kind) || binary.BigEndian.Uint32(raw[5:9]) != 5 {
			t.Fatalf("wrong wire encoding: %x", raw)
		}
		got, err := codec.ReadFrame()
		if err != nil || got.StreamID != want.StreamID || got.Type != kind || !bytes.Equal(got.Payload, want.Payload) {
			t.Fatalf("roundtrip: %+v %v", got, err)
		}
	}
}
func TestFrameRejectsMalformed(t *testing.T) {
	for _, raw := range [][]byte{{1}, {0, 0, 0, 1, 1, 0, 0, 0, 2, 1}, {0, 0, 0, 1, 99, 0, 0, 0, 0}, {0, 0, 0, 1, 1, 255, 255, 255, 255}} {
		if _, err := NewCodec(bytes.NewReader(raw), io.Discard).ReadFrame(); err == nil {
			t.Fatalf("accepted malformed frame %x", raw)
		}
	}
	if err := NewCodec(nil, io.Discard).WriteFrame(Frame{Type: Data, Payload: make([]byte, MaxPayload+1)}); err == nil {
		t.Fatal("accepted oversized payload")
	}
}
func TestConcurrentFrameWriters(t *testing.T) {
	var b bytes.Buffer
	c := NewCodec(&b, &b)
	var wg sync.WaitGroup
	for i := range 32 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for range 20 {
				if err := c.WriteFrame(Frame{uint32(i + 1), Data, bytes.Repeat([]byte{byte(i)}, 257)}); err != nil {
					t.Error(err)
				}
			}
		}()
	}
	wg.Wait()
	for range 640 {
		f, err := c.ReadFrame()
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(f.Payload, bytes.Repeat([]byte{byte(f.StreamID - 1)}, 257)) {
			t.Fatal("interleaved frames")
		}
	}
}
func TestStreamReadWriteCloseAndBounds(t *testing.T) {
	var b bytes.Buffer
	s := NewStream(7, NewCodec(&b, &b))
	payload := bytes.Repeat([]byte{0, 255, 23}, 30000)
	if n, err := s.Write(payload); err != nil || n != len(payload) {
		t.Fatalf("write %d %v", n, err)
	}
	var got []byte
	for len(got) < len(payload) {
		f, err := s.codec.ReadFrame()
		if err != nil {
			t.Fatal(err)
		}
		got = append(got, f.Payload...)
	}
	if !bytes.Equal(got, payload) {
		t.Fatal("write corrupted")
	}
	if err := s.Receive(Frame{7, Data, payload}); err != nil {
		t.Fatal(err)
	}
	if err := s.Receive(Frame{StreamID: 7, Type: Close}); err != nil {
		t.Fatal(err)
	}
	got, err := io.ReadAll(s)
	if err != nil || !bytes.Equal(got, payload) {
		t.Fatalf("read %v", err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Write([]byte("late")); err == nil {
		t.Fatal("write after close")
	}
	bounded := NewStream(8, s.codec)
	for range 4 {
		if err := bounded.Receive(Frame{8, Data, make([]byte, MaxPayload)}); err != nil {
			t.Fatal(err)
		}
	}
	if err := bounded.Receive(Frame{8, Data, []byte{1}}); err == nil {
		t.Fatal("unbounded stream buffer")
	}
}

type echoShell struct {
	net.Conn
	remote net.Conn
	sizes  chan [2]int
}

func (s *echoShell) Stderr() io.Reader     { return bytes.NewReader(nil) }
func (s *echoShell) Resize(c, r int) error { s.sizes <- [2]int{c, r}; return nil }
func (s *echoShell) Close() error          { _ = s.remote.Close(); return s.Conn.Close() }

type localFS struct{ root string }

func (f *localFS) path(p string) string { return filepath.Join(f.root, p) }
func (f *localFS) ListDir(p string) ([]os.FileInfo, error) {
	entries, err := os.ReadDir(f.path(p))
	if err != nil {
		return nil, err
	}
	var result []os.FileInfo
	for _, e := range entries {
		info, err := e.Info()
		if err != nil {
			return nil, err
		}
		result = append(result, info)
	}
	return result, nil
}
func (f *localFS) Stat(p string) (os.FileInfo, error)     { return os.Stat(f.path(p)) }
func (f *localFS) Read(p string) (io.ReadCloser, error)   { return os.Open(f.path(p)) }
func (f *localFS) Write(p string) (io.WriteCloser, error) { return os.Create(f.path(p)) }
func (f *localFS) Mkdir(p string) error                   { return os.Mkdir(f.path(p), 0700) }
func (f *localFS) Remove(p string) error                  { return os.Remove(f.path(p)) }
func (f *localFS) Rename(a, b string) error               { return os.Rename(f.path(a), f.path(b)) }
func (f *localFS) Close() error                           { return nil }

type localSession struct {
	shell  *echoShell
	fs     *localFS
	closed chan struct{}
	once   sync.Once
}

func (s *localSession) OpenShell(transport.PTYOptions) (Shell, error) { return s.shell, nil }
func (s *localSession) OpenSFTP() (FileSystem, error)                 { return s.fs, nil }
func (s *localSession) Close() error {
	s.once.Do(func() { close(s.closed); _ = s.shell.Close() })
	return nil
}

func TestUnixSocketRPCAndStreams(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Unix socket integration")
	}
	path := filepath.Join(t.TempDir(), "daemon.sock")
	l, err := Listen(path)
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	if _, err := Listen(path); err == nil {
		t.Fatal("replaced active socket")
	}
	a, b := net.Pipe()
	shell := &echoShell{Conn: a, remote: b, sizes: make(chan [2]int, 2)}
	go func() { _, _ = io.Copy(b, b) }()
	session := &localSession{shell: shell, fs: &localFS{t.TempDir()}, closed: make(chan struct{})}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	shutdown := make(chan struct{}, 1)
	go func() {
		conn, err := l.Accept()
		if err != nil {
			done <- err
			return
		}
		done <- (&Router{Connect: func(context.Context, ConnectParams) (Session, error) { return session, nil }, Shutdown: func() { shutdown <- struct{}{} }}).Serve(ctx, conn)
	}()
	conn, err := net.Dial("unix", path)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(10 * time.Second))
	c := NewCodec(conn, conn)
	read := func() Frame {
		t.Helper()
		f, err := c.ReadFrame()
		if err != nil {
			t.Fatal(err)
		}
		return f
	}
	write := func(f Frame) {
		t.Helper()
		if err := c.WriteFrame(f); err != nil {
			t.Fatal(err)
		}
	}
	rpc := func(method string, params any) json.RawMessage {
		t.Helper()
		data, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": 42, "method": method, "params": params})
		write(Frame{Type: JSONRPC, Payload: data})
		f := read()
		if f.Type != JSONRPC {
			t.Fatalf("expected rpc, got %+v", f)
		}
		var res struct {
			ID     int             `json:"id"`
			Result json.RawMessage `json:"result"`
			Error  *RPCError       `json:"error"`
		}
		if err := json.Unmarshal(f.Payload, &res); err != nil || res.Error != nil || res.ID != 42 {
			t.Fatalf("rpc %s: %s (%v)", method, f.Payload, err)
		}
		return res.Result
	}
	if !bytes.Contains(rpc("daemon.ping", nil), []byte("true")) {
		t.Fatal("ping")
	}
	var connected struct {
		SessionID string `json:"sessionId"`
		StreamID  uint32 `json:"streamId"`
	}
	if err := json.Unmarshal(rpc("session.connect", map[string]any{"host": "example", "user": "test", "cols": 80, "rows": 24}), &connected); err != nil {
		t.Fatal(err)
	}
	args := map[string]any{"sessionId": connected.SessionID}
	binaryData := []byte{0, 255, 17, 0, 128}
	write(Frame{connected.StreamID, Data, binaryData})
	f := read()
	if f.StreamID != connected.StreamID || f.Type != Data || !bytes.Equal(f.Payload, binaryData) {
		t.Fatalf("echo %+v", f)
	}
	write(Frame{connected.StreamID, WindowResize, []byte(`{"cols":120,"rows":40}`)})
	select {
	case size := <-shell.sizes:
		if size != [2]int{120, 40} {
			t.Fatal(size)
		}
	case <-time.After(time.Second):
		t.Fatal("no resize")
	}
	rpc("session.resize", map[string]any{"sessionId": connected.SessionID, "cols": 90, "rows": 30})
	if size := <-shell.sizes; size != [2]int{90, 30} {
		t.Fatal(size)
	}
	rpc("sftp.open", args)
	rpc("sftp.mkdir", map[string]any{"sessionId": connected.SessionID, "path": "dir"})
	var transfer struct {
		StreamID uint32 `json:"streamId"`
	}
	_ = json.Unmarshal(rpc("sftp.upload_stream", map[string]any{"sessionId": connected.SessionID, "path": "dir/file"}), &transfer)
	write(Frame{transfer.StreamID, Data, binaryData})
	write(Frame{StreamID: transfer.StreamID, Type: Close})
	f = read()
	if f.Type != Close || f.StreamID != transfer.StreamID {
		t.Fatalf("upload completion %+v", f)
	}
	content, err := os.ReadFile(session.fs.path("dir/file"))
	if err != nil || !bytes.Equal(content, binaryData) {
		t.Fatalf("upload bytes %x %v", content, err)
	}
	var stat FileInfo
	_ = json.Unmarshal(rpc("sftp.stat", map[string]any{"sessionId": connected.SessionID, "path": "dir/file"}), &stat)
	if stat.Name != "file" || stat.Size != int64(len(binaryData)) || stat.IsDir || stat.Mtime == 0 {
		t.Fatalf("stat %+v", stat)
	}
	var files []FileInfo
	_ = json.Unmarshal(rpc("sftp.list", map[string]any{"sessionId": connected.SessionID, "path": "dir"}), &files)
	if len(files) != 1 || files[0].Name != "file" {
		t.Fatal(files)
	}
	_ = json.Unmarshal(rpc("sftp.download_stream", map[string]any{"sessionId": connected.SessionID, "path": "dir/file"}), &transfer)
	var downloaded []byte
	for {
		f = read()
		if f.StreamID != transfer.StreamID {
			t.Fatal("wrong download stream")
		}
		if f.Type == Close {
			break
		}
		if f.Type != Data {
			t.Fatal(f)
		}
		downloaded = append(downloaded, f.Payload...)
	}
	if !bytes.Equal(downloaded, binaryData) {
		t.Fatal("download corrupt")
	}
	rpc("sftp.rename", map[string]any{"sessionId": connected.SessionID, "oldPath": "dir/file", "newPath": "dir/renamed"})
	rpc("sftp.remove", map[string]any{"sessionId": connected.SessionID, "path": "dir/renamed", "isDir": false})
	rpc("sftp.remove", map[string]any{"sessionId": connected.SessionID, "path": "dir", "isDir": true})
	write(Frame{StreamID: connected.StreamID, Type: Close})
	f = read()
	if f.Type != Close || f.StreamID != connected.StreamID {
		t.Fatalf("terminal close %+v", f)
	}
	rpc("session.disconnect", args)
	f = read()
	if f.Type != Close {
		t.Fatal(f)
	}
	rpc("daemon.shutdown", nil)
	select {
	case <-shutdown:
	case <-time.After(time.Second):
		t.Fatal("shutdown not dispatched")
	}
	_ = conn.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("router leaked")
	}
	select {
	case <-session.closed:
	default:
		t.Fatal("session not closed")
	}
	_ = l.Close()
	if _, err := os.Lstat(path); !os.IsNotExist(err) {
		t.Fatal("socket not removed")
	}
}

func TestRPCErrorsAndNotification(t *testing.T) {
	r := &Router{}
	p := &peer{}
	for _, test := range []struct {
		body string
		code int
	}{
		{`{`, -32700}, {`[]`, -32600}, {`{"jsonrpc":"1.0","id":1,"method":"daemon.ping"}`, -32600},
		{`{"jsonrpc":"2.0","id":{},"method":"daemon.ping"}`, -32600}, {`{"jsonrpc":"2.0","id":1,"method":"missing"}`, -32601},
		{`{"jsonrpc":"2.0","id":1,"method":"session.connect","params":{"cols":0}}`, -32602},
	} {
		res, _ := r.dispatch(p, []byte(test.body))
		if res == nil || res.Error == nil || res.Error.Code != test.code {
			t.Fatalf("%s: %+v", test.body, res)
		}
	}
	if res, _ := r.dispatch(p, []byte(`{"jsonrpc":"2.0","method":"daemon.ping"}`)); res != nil {
		t.Fatal("notification response")
	}
}

func TestRouterCancellationClosesActiveSession(t *testing.T) {
	server, client := net.Pipe()
	a, b := net.Pipe()
	session := &localSession{shell: &echoShell{Conn: a, remote: b, sizes: make(chan [2]int, 1)}, fs: &localFS{t.TempDir()}, closed: make(chan struct{})}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	defer client.Close()
	done := make(chan error, 1)
	go func() {
		done <- (&Router{Connect: func(context.Context, ConnectParams) (Session, error) { return session, nil }}).Serve(ctx, server)
	}()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	c := NewCodec(client, client)
	if err := c.WriteFrame(Frame{Type: JSONRPC, Payload: []byte(`{"jsonrpc":"2.0","id":1,"method":"session.connect","params":{"cols":80,"rows":24}}`)}); err != nil {
		t.Fatal(err)
	}
	f, err := c.ReadFrame()
	if err != nil || !bytes.Contains(f.Payload, []byte(`"sessionId"`)) {
		t.Fatalf("connect %s %v", f.Payload, err)
	}
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("cancellation did not release session and stream goroutines")
	}
	select {
	case <-session.closed:
	default:
		t.Fatal("active SSH session leaked")
	}
}
