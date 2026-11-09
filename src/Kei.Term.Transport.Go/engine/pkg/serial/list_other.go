//go:build !linux && !darwin && !windows

package serial

import (
	"context"
	"errors"
)

func ListPorts(ctx context.Context) ([]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	return nil, errors.New("serial port discovery is not supported on this platform")
}
