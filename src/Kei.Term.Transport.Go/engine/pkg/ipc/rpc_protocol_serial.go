package ipc

import (
	"context"
	"encoding/json"
	"io"

	"github.com/keiterm/transport-engine/pkg/serial"
)

func openSerial(ctx context.Context, raw json.RawMessage) (io.ReadWriteCloser, error) {
	var options serial.Options
	if json.Unmarshal(raw, &options) != nil {
		return nil, &RPCError{-32602, "Invalid serial params"}
	}
	// ctx is the peer lifetime, not a short-lived connect timeout.
	return serial.Open(ctx, options)
}
func controlSerial(port io.ReadWriteCloser, method string, enabled bool) error {
	p, ok := port.(*serial.Port)
	if !ok {
		return &RPCError{-32003, "Terminal does not support serial control"}
	}
	switch method {
	case "serial.setDtr":
		return p.SetDTR(enabled)
	case "serial.setRts":
		return p.SetRTS(enabled)
	case "serial.setBreak":
		return p.SetBreak(enabled)
	}
	return &RPCError{-32601, "Method not found"}
}
