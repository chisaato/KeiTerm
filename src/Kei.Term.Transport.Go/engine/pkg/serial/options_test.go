package serial

import (
	"context"
	"errors"
	"testing"
)

func TestInvalidOptionsAndCanceledOpen(t *testing.T) {
	b := true
	for _, o := range []Options{
		{}, {PortName: " "}, {PortName: "x\x00y"}, {PortName: "x", BaudRate: -1},
		{PortName: "x", DataBits: 9}, {PortName: "x", Parity: "Bogus"},
		{PortName: "x", StopBits: "Bogus"}, {PortName: "x", FlowControl: "Bogus"},
		{PortName: "x", FlowControl: "RtsCts", RtsEnable: &b},
	} {
		if p, err := Open(context.Background(), o); !errors.Is(err, ErrInvalidOptions) || p != nil {
			t.Fatalf("%+v: %v %v", o, p, err)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if p, err := Open(ctx, Options{PortName: "not-a-port"}); !errors.Is(err, context.Canceled) || p != nil {
		t.Fatalf("%v %v", p, err)
	}
	if p, err := Open(context.Background(), Options{PortName: "/nonexistent/keiterm-serial"}); err == nil || p != nil {
		t.Fatalf("%v %v", p, err)
	}
}
