package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"io"
	"log"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/keiterm/transport-engine/pkg/ipc"
)

func TestServeDiagnosticsProtocolFailureDoesNotLogPayload(t *testing.T) {
	server, client := net.Pipe()
	defer client.Close()
	var output bytes.Buffer
	done := make(chan struct{})
	go func() {
		serveConnection(context.Background(), server, &ipc.Router{}, log.New(&output, "", 0), 7)
		close(done)
	}()
	header := make([]byte, 9)
	binary.BigEndian.PutUint32(header, 42)
	header[4] = byte(ipc.JSONRPC)
	payload := []byte(`{"password":"password-secret","privateKey":"key-secret","payload":"terminal-secret"}`)
	binary.BigEndian.PutUint32(header[5:], uint32(len(payload)))
	if _, err := client.Write(append(header, payload...)); err != nil {
		t.Fatal(err)
	}
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("router did not finish")
	}
	if !strings.Contains(output.String(), `"event":"router-ended"`) || !strings.Contains(output.String(), `"reason":"protocol"`) {
		t.Fatal(output.String())
	}
	for _, secret := range []string{"password-secret", "key-secret", "terminal-secret"} {
		if strings.Contains(output.String(), secret) {
			t.Fatal("diagnostic leaked payload")
		}
	}
	if _, err := client.Read(make([]byte, 1)); err != io.EOF {
		t.Fatalf("connection not closed: %v", err)
	}
}

func TestServeDiagnosticsCancellationAndEOF(t *testing.T) {
	for _, canceled := range []bool{false, true} {
		server, client := net.Pipe()
		ctx, cancel := context.WithCancel(context.Background())
		var output bytes.Buffer
		done := make(chan struct{})
		go func() {
			serveConnection(ctx, server, &ipc.Router{}, log.New(&output, "", 0), 9)
			close(done)
		}()
		if canceled {
			cancel()
		} else {
			client.Close()
		}
		select {
		case <-done:
		case <-time.After(3 * time.Second):
			t.Fatal("router did not finish")
		}
		reason := "eof"
		if canceled {
			reason = "canceled"
		}
		if !strings.Contains(output.String(), `"reason":"`+reason+`"`) {
			t.Fatal(output.String())
		}
		client.Close()
		cancel()
	}
}
