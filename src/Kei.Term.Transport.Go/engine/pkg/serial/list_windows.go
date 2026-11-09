//go:build windows

package serial

import (
	"context"
	"errors"
	"golang.org/x/sys/windows/registry"
	"sort"
)

func ListPorts(ctx context.Context) ([]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, `HARDWARE\DEVICEMAP\SERIALCOMM`, registry.QUERY_VALUE)
	if errors.Is(err, registry.ErrNotExist) {
		return []string{}, nil
	}
	if err != nil {
		return nil, err
	}
	defer key.Close()
	values, err := key.ReadValueNames(-1)
	if err != nil {
		return nil, err
	}
	names := []string{}
	seen := map[string]bool{}
	for _, value := range values {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		name, _, err := key.GetStringValue(value)
		if err != nil {
			return nil, err
		}
		if name != "" && !seen[name] {
			names = append(names, name)
			seen[name] = true
		}
	}
	sort.Strings(names)
	return names, nil
}
