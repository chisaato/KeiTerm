//go:build linux || darwin

package serial

import (
	"context"
	"os"
	"runtime"
	"sort"
	"strings"
)

// ListPorts discovers basic device names, not USB metadata or accessible hardware.
func ListPorts(ctx context.Context) ([]string, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	entries, err := os.ReadDir("/dev")
	if err != nil {
		return nil, err
	}
	names := []string{}
	for _, entry := range entries {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		if entry.IsDir() {
			continue
		}
		n := entry.Name()
		prefixes := []string{"ttyS", "ttyUSB", "ttyACM", "ttyAMA", "ttyAP", "ttyTHS", "ttyXRUSB", "rfcomm"}
		if runtime.GOOS == "darwin" {
			prefixes = []string{"cu."}
		}
		for _, prefix := range prefixes {
			if strings.HasPrefix(n, prefix) {
				names = append(names, "/dev/"+n)
				break
			}
		}
	}
	sort.Strings(names)
	return names, nil
}
