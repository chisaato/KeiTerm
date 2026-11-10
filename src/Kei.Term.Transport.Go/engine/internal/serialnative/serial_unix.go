//
// Copyright 2014-2026 Cristian Maglie. All rights reserved.
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file.
//

//go:build linux || darwin

package serial

import (
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
	"time"

	"github.com/keiterm/transport-engine/internal/serialnative/unixutils"
	"golang.org/x/sys/unix"
)

type unixPort struct {
	handle int

	readTimeout time.Duration
	closeLock   sync.RWMutex
	closeSignal *unixutils.Pipe
	opened      uint32
}

func (port *unixPort) Close() error {
	if !atomic.CompareAndSwapUint32(&port.opened, 1, 0) {
		return nil
	}

	if port.closeSignal != nil {
		// Wake both reads and writes before waiting; keep the descriptor alive
		// until all operations leave, preventing descriptor-reuse races.
		port.closeSignal.Write([]byte{0})
	}
	port.closeLock.Lock()
	defer port.closeLock.Unlock()
	_ = port.releaseExclusiveAccess()
	err := unix.Close(port.handle)
	if port.closeSignal != nil {
		err = errors.Join(err, port.closeSignal.Close())
	}
	return err
}

func (port *unixPort) Read(p []byte) (int, error) {
	port.closeLock.RLock()
	defer port.closeLock.RUnlock()
	if atomic.LoadUint32(&port.opened) != 1 {
		return 0, &PortError{code: PortClosed}
	}

	var deadline time.Time
	if port.readTimeout != NoTimeout {
		deadline = time.Now().Add(port.readTimeout)
	}

	for {
		timeout := time.Duration(-1)
		if port.readTimeout != NoTimeout {
			timeout = time.Until(deadline)
			if timeout < 0 {
				// a negative timeout means "no-timeout" in Select(...)
				timeout = 0
			}
		}
		ready, err := port.wait(unix.POLLIN, timeout)
		if err != nil {
			return 0, err
		}
		if !ready {
			// Timeout happened
			return 0, nil
		}
		n, err := unix.Read(port.handle, p)
		if err == unix.EINTR || err == unix.EAGAIN {
			continue
		}
		// Linux: when the port is disconnected during a read operation
		// the port is left in a "readable with zero-length-data" state.
		// https://stackoverflow.com/a/34945814/1655275
		if n == 0 && err == nil {
			return 0, &PortError{code: PortClosed}
		}
		if n < 0 { // Do not return -1 unix errors
			n = 0
		}
		return n, err
	}
}

func (port *unixPort) Write(p []byte) (n int, err error) {
	port.closeLock.RLock()
	defer port.closeLock.RUnlock()
	if atomic.LoadUint32(&port.opened) != 1 {
		return 0, &PortError{code: PortClosed}
	}
	for n < len(p) {
		if _, err = port.wait(unix.POLLOUT, NoTimeout); err != nil {
			return n, err
		}
		written, e := unix.Write(port.handle, p[n:])
		if written > 0 {
			n += written
		}
		if e == unix.EINTR || e == unix.EAGAIN {
			continue
		}
		if e != nil {
			return n, e
		}
	}
	return
}

func (port *unixPort) wait(events int16, timeout time.Duration) (bool, error) {
	fds := []unix.PollFd{{Fd: int32(port.handle), Events: events}, {Fd: int32(port.closeSignal.ReadFD()), Events: unix.POLLIN}}
	ms := -1
	if timeout >= 0 {
		ms = int((timeout + time.Millisecond - 1) / time.Millisecond)
	}
	for {
		n, err := unix.Poll(fds, ms)
		if err == unix.EINTR {
			continue
		}
		if err != nil {
			return false, err
		}
		if fds[1].Revents != 0 || atomic.LoadUint32(&port.opened) != 1 {
			return false, &PortError{code: PortClosed}
		}
		if fds[0].Revents&(unix.POLLERR|unix.POLLHUP|unix.POLLNVAL) != 0 {
			return false, &PortError{code: PortClosed}
		}
		return n > 0, nil
	}
}

func (port *unixPort) SetBreakState(on bool) error {
	if on {
		return unix.IoctlSetInt(port.handle, ioctlTiocsbrk, 0)
	}
	return unix.IoctlSetInt(port.handle, ioctlTioccbrk, 0)
}

func (port *unixPort) Break(t time.Duration) error {
	if err := unix.IoctlSetInt(port.handle, ioctlTiocsbrk, 0); err != nil {
		return err
	}

	time.Sleep(t)

	if err := unix.IoctlSetInt(port.handle, ioctlTioccbrk, 0); err != nil {
		return err
	}

	return nil
}

func (port *unixPort) SetMode(mode *Mode) error {
	settings, err := port.getTermSettings()
	if err != nil {
		return err
	}
	if err := setTermSettingsParity(mode.Parity, settings); err != nil {
		return err
	}
	if err := setTermSettingsDataBits(mode.DataBits, settings); err != nil {
		return err
	}
	if err := setTermSettingsStopBits(mode.StopBits, settings); err != nil {
		return err
	}
	setFlowControl(mode.FlowControl, settings)
	requireSpecialBaudrate := false
	if err, special := setTermSettingsBaudrate(mode.BaudRate, settings); err != nil {
		return err
	} else if special {
		requireSpecialBaudrate = true
	}
	if err := port.setTermSettings(settings); err != nil {
		return err
	}
	if requireSpecialBaudrate {
		// MacOSX require this one to be the last operation otherwise an
		// 'Invalid serial port' error is produced.
		if err := port.setSpecialBaudrate(uint32(mode.BaudRate)); err != nil {
			return err
		}
	}
	return nil
}

func (port *unixPort) SetDTR(dtr bool) error {
	status, err := port.getModemBitsStatus()
	if err != nil {
		return err
	}
	if dtr {
		status |= unix.TIOCM_DTR
	} else {
		status &^= unix.TIOCM_DTR
	}
	return port.setModemBitsStatus(status)
}

func (port *unixPort) SetRTS(rts bool) error {
	status, err := port.getModemBitsStatus()
	if err != nil {
		return err
	}
	if rts {
		status |= unix.TIOCM_RTS
	} else {
		status &^= unix.TIOCM_RTS
	}
	return port.setModemBitsStatus(status)
}

func (port *unixPort) SetReadTimeout(timeout time.Duration) error {
	if timeout < 0 && timeout != NoTimeout {
		return &PortError{code: InvalidTimeoutValue}
	}
	port.readTimeout = timeout
	return nil
}

func (port *unixPort) GetModemStatusBits() (*ModemStatusBits, error) {
	status, err := port.getModemBitsStatus()
	if err != nil {
		return nil, err
	}
	return &ModemStatusBits{
		CTS: (status & unix.TIOCM_CTS) != 0,
		DCD: (status & unix.TIOCM_CD) != 0,
		DSR: (status & unix.TIOCM_DSR) != 0,
		RI:  (status & unix.TIOCM_RI) != 0,
	}, nil
}

func nativeOpen(portName string, mode *Mode) (*unixPort, error) {
	h, err := unix.Open(portName, unix.O_RDWR|unix.O_NOCTTY|unix.O_NDELAY|unix.O_CLOEXEC, 0)
	if err != nil {
		switch err {
		case unix.EBUSY:
			return nil, &PortError{code: PortBusy}
		case unix.EACCES:
			return nil, &PortError{code: PermissionDenied}
		}
		return nil, err
	}
	port := &unixPort{
		handle:      h,
		opened:      1,
		readTimeout: NoTimeout,
	}

	// Setup serial port
	settings, err := port.getTermSettings()
	if err != nil {
		port.Close()
		return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error getting term settings: %w", err)}
	}

	// Set raw mode
	setRawMode(settings)

	// Explicitly disable RTS/CTS flow control
	setTermSettingsCtsRts(false, settings)

	if err = port.setTermSettings(settings); err != nil {
		port.Close()
		return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error setting term settings: %w", err)}
	}

	if mode.InitialStatusBits != nil {
		status, err := port.getModemBitsStatus()
		if err != nil {
			port.Close()
			return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error getting modem bits status: %w", err)}
		}
		if mode.InitialStatusBits.DTR != nil {
			if *mode.InitialStatusBits.DTR {
				status |= unix.TIOCM_DTR
			} else {
				status &^= unix.TIOCM_DTR
			}
		}
		if mode.InitialStatusBits.RTS != nil {
			if *mode.InitialStatusBits.RTS {
				status |= unix.TIOCM_RTS
			} else {
				status &^= unix.TIOCM_RTS
			}
		}
		if err := port.setModemBitsStatus(status); err != nil {
			port.Close()
			return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error setting modem bits status: %w", err)}
		}
	}

	// MacOSX require that this operation is the last one otherwise an
	// 'Invalid serial port' error is returned... don't know why...
	if err := port.SetMode(mode); err != nil {
		port.Close()
		return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error configuring port: %w", err)}
	}

	// Keep O_NONBLOCK: poll + close pipe must also interrupt paused writes.

	port.acquireExclusiveAccess()

	// This pipe is used as a signal to cancel blocking Read
	if pipe, err := unixutils.NewPipe(); err != nil {
		port.Close()
		return nil, &PortError{code: InvalidSerialPort, causedBy: fmt.Errorf("error opening signaling pipe: %w", err)}
	} else {
		port.closeSignal = pipe
	}

	return port, nil
}

// termios manipulation functions

func setTermSettingsParity(parity Parity, settings *unix.Termios) error {
	switch parity {
	case NoParity:
		settings.Cflag &^= unix.PARENB
		settings.Cflag &^= unix.PARODD
		settings.Cflag &^= tcCMSPAR
		settings.Iflag &^= unix.INPCK
	case OddParity:
		settings.Cflag |= unix.PARENB
		settings.Cflag |= unix.PARODD
		settings.Cflag &^= tcCMSPAR
		settings.Iflag |= unix.INPCK
	case EvenParity:
		settings.Cflag |= unix.PARENB
		settings.Cflag &^= unix.PARODD
		settings.Cflag &^= tcCMSPAR
		settings.Iflag |= unix.INPCK
	case MarkParity:
		if tcCMSPAR == 0 {
			return &PortError{code: InvalidParity}
		}
		settings.Cflag |= unix.PARENB
		settings.Cflag |= unix.PARODD
		settings.Cflag |= tcCMSPAR
		settings.Iflag |= unix.INPCK
	case SpaceParity:
		if tcCMSPAR == 0 {
			return &PortError{code: InvalidParity}
		}
		settings.Cflag |= unix.PARENB
		settings.Cflag &^= unix.PARODD
		settings.Cflag |= tcCMSPAR
		settings.Iflag |= unix.INPCK
	default:
		return &PortError{code: InvalidParity}
	}
	return nil
}

func setTermSettingsDataBits(bits int, settings *unix.Termios) error {
	databits, ok := databitsMap[bits]
	if !ok {
		return &PortError{code: InvalidDataBits}
	}
	// Remove previous databits setting
	settings.Cflag &^= unix.CSIZE
	// Set requested databits
	settings.Cflag |= databits
	return nil
}

func setTermSettingsStopBits(bits StopBits, settings *unix.Termios) error {
	switch bits {
	case OneStopBit:
		settings.Cflag &^= unix.CSTOPB
	case OnePointFiveStopBits:
		return &PortError{code: InvalidStopBits}
	case TwoStopBits:
		settings.Cflag |= unix.CSTOPB
	default:
		return &PortError{code: InvalidStopBits}
	}
	return nil
}

func setTermSettingsCtsRts(enable bool, settings *unix.Termios) {
	if enable {
		settings.Cflag |= tcCRTSCTS
	} else {
		settings.Cflag &^= tcCRTSCTS
	}
}

func setFlowControl(flow string, settings *unix.Termios) {
	setTermSettingsCtsRts(flow == "RtsCts", settings)
	settings.Iflag &^= unix.IXON | unix.IXOFF | unix.IXANY
	if flow == "XonXoff" {
		settings.Iflag |= unix.IXON | unix.IXOFF
		settings.Cc[unix.VSTART], settings.Cc[unix.VSTOP] = 17, 19
	}
}

func setRawMode(settings *unix.Termios) {
	// Set local mode
	settings.Cflag |= unix.CREAD
	settings.Cflag |= unix.CLOCAL

	// Set raw mode
	settings.Lflag &^= unix.ICANON
	settings.Lflag &^= unix.ECHO
	settings.Lflag &^= unix.ECHOE
	settings.Lflag &^= unix.ECHOK
	settings.Lflag &^= unix.ECHONL
	settings.Lflag &^= unix.ECHOCTL
	settings.Lflag &^= unix.ECHOPRT
	settings.Lflag &^= unix.ECHOKE
	settings.Lflag &^= unix.ISIG
	settings.Lflag &^= unix.IEXTEN

	settings.Iflag &^= unix.IXON
	settings.Iflag &^= unix.IXOFF
	settings.Iflag &^= unix.IXANY
	settings.Iflag &^= unix.INPCK
	settings.Iflag &^= unix.IGNPAR
	settings.Iflag &^= unix.PARMRK
	settings.Iflag &^= unix.ISTRIP
	settings.Iflag &^= unix.IGNBRK
	settings.Iflag &^= unix.BRKINT
	settings.Iflag &^= unix.INLCR
	settings.Iflag &^= unix.IGNCR
	settings.Iflag &^= unix.ICRNL
	settings.Iflag &^= tcIUCLC

	settings.Oflag &^= unix.OPOST

	// Block reads until at least one char is available (no timeout)
	settings.Cc[unix.VMIN] = 1
	settings.Cc[unix.VTIME] = 0
}

// native syscall wrapper functions

func (port *unixPort) getTermSettings() (*unix.Termios, error) {
	return unix.IoctlGetTermios(port.handle, ioctlTcgetattr)
}

func (port *unixPort) setTermSettings(settings *unix.Termios) error {
	return unix.IoctlSetTermios(port.handle, ioctlTcsetattr, settings)
}

func (port *unixPort) getModemBitsStatus() (int, error) {
	return unix.IoctlGetInt(port.handle, unix.TIOCMGET)
}

func (port *unixPort) setModemBitsStatus(status int) error {
	return unix.IoctlSetPointerInt(port.handle, unix.TIOCMSET, status)
}

func (port *unixPort) acquireExclusiveAccess() error {
	return unix.IoctlSetInt(port.handle, unix.TIOCEXCL, 0)
}

func (port *unixPort) releaseExclusiveAccess() error {
	return unix.IoctlSetInt(port.handle, unix.TIOCNXCL, 0)
}
