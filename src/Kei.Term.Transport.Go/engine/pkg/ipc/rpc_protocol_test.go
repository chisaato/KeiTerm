package ipc

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"
)

func TestProtocolTCPRemoteCloseAndStreamErrorLeavePeerAlive(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	server, client := net.Pipe()
	client.SetDeadline(time.Now().Add(5 * time.Second))
	defer client.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	ended := make(chan struct{})
	go func() { defer close(ended); _ = (&Router{}).Serve(ctx, server) }()
	codec := NewCodec(client, client)
	call := func(id int, method string, params any) Response {
		t.Helper()
		payload, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": id, "method": method, "params": params})
		if err := codec.WriteFrame(Frame{Type: JSONRPC, Payload: payload}); err != nil {
			t.Fatal(err)
		}
		frame, err := codec.ReadFrame()
		if err != nil {
			t.Fatal(err)
		}
		if frame.Type != JSONRPC {
			t.Fatalf("unexpected frame %+v", frame)
		}
		var response Response
		if err := json.Unmarshal(frame.Payload, &response); err != nil {
			t.Fatal(err)
		}
		return response
	}
	for i := 1; i <= 2; i++ {
		response := call(i, "telnet.connect", map[string]any{"host": "127.0.0.1", "port": listener.Addr().(*net.TCPAddr).Port})
		if response.Error != nil {
			t.Fatal(response.Error)
		}
		peer, err := listener.Accept()
		if err != nil {
			t.Fatal(err)
		}
		values := response.Result.(map[string]any)
		id := uint32(values["streamId"].(float64))
		files := call(i+20, "sftp.open", map[string]any{"sessionId": values["sessionId"]})
		if files.Error == nil {
			t.Fatal("Telnet unexpectedly acquired an SSH filesystem")
		}
		if i == 1 {
			peer.Close()
		} else {
			if err := codec.WriteFrame(Frame{StreamID: id, Type: Error, Payload: []byte("single stream canceled")}); err != nil {
				t.Fatal(err)
			}
		}
		// One Error (on failure only) followed by Close, never IPC termination.
		for {
			f, err := codec.ReadFrame()
			if err != nil {
				t.Fatal(err)
			}
			if f.StreamID != id {
				t.Fatal("wrong stream")
			}
			if f.Type == Close {
				break
			}
			if f.Type != Error {
				t.Fatalf("unexpected %v", f.Type)
			}
		}
		peer.Close()
		if err := codec.WriteFrame(Frame{StreamID: id, Type: Close}); err != nil {
			t.Fatal(err)
		}
		ping := call(i+10, "daemon.ping", nil)
		if ping.Error != nil {
			t.Fatal(ping.Error)
		}
	}
	cancel()
	select {
	case <-ended:
	case <-time.After(time.Second):
		t.Fatal("router did not end")
	}
}
