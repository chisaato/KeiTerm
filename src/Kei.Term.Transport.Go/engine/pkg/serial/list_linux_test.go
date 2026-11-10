package serial

import (
	"context"
	"os"
	"reflect"
	"sort"
	"strings"
	"testing"
)

func TestListPortsMatchesLocalDevices(t *testing.T) {
	entries, err := os.ReadDir("/dev")
	if err != nil {
		t.Fatal(err)
	}
	expected := []string{}
	for _, entry := range entries {
		for _, prefix := range []string{"ttyS", "ttyUSB", "ttyACM", "ttyAMA", "ttyAP", "ttyTHS", "ttyXRUSB", "rfcomm"} {
			if !entry.IsDir() && strings.HasPrefix(entry.Name(), prefix) {
				expected = append(expected, "/dev/"+entry.Name())
				break
			}
		}
	}
	sort.Strings(expected)
	actual, err := ListPorts(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(expected, actual) {
		t.Fatalf("expected %v got %v", expected, actual)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := ListPorts(ctx); err != context.Canceled {
		t.Fatalf("cancellation: %v", err)
	}
}
