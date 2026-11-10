//go:build !windows

package main

import (
	"os"
	"syscall"
)

func parentAlive(pid int) bool {
	if os.Getppid() != pid {
		return false
	}
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}
