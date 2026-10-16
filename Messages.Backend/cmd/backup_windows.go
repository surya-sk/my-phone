//go:build windows

package cmd

import (
	"errors"
	"fmt"
	"os"
	"syscall"
	"unsafe"
)

var (
	kernel32                = syscall.NewLazyDLL("kernel32.dll")
	procLockFileEx          = kernel32.NewProc("LockFileEx")
	procUnlockFileEx        = kernel32.NewProc("UnlockFileEx")
	procGetDiskFreeSpaceExW = kernel32.NewProc("GetDiskFreeSpaceExW")
)

func lockInstanceFile(file *os.File) error {
	var overlapped syscall.Overlapped
	const lockExclusive = 0x00000002
	const lockFailImmediately = 0x00000001
	result, _, err := procLockFileEx.Call(file.Fd(), lockExclusive|lockFailImmediately, 0, 1, 0, uintptr(unsafe.Pointer(&overlapped)))
	if result == 0 {
		return err
	}
	return nil
}

func unlockInstanceFile(file *os.File) error {
	var overlapped syscall.Overlapped
	result, _, err := procUnlockFileEx.Call(file.Fd(), 0, 1, 0, uintptr(unsafe.Pointer(&overlapped)))
	if result == 0 {
		return err
	}
	return nil
}

func isInstanceLockHeld(err error) bool { return errors.Is(err, syscall.Errno(33)) }

func filesystemAvailableBytes(path string) (uint64, error) {
	wide, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return 0, err
	}
	var available uint64
	result, _, callErr := procGetDiskFreeSpaceExW.Call(uintptr(unsafe.Pointer(wide)), uintptr(unsafe.Pointer(&available)), 0, 0)
	if result == 0 {
		return 0, fmt.Errorf("disk free space: %w", callErr)
	}
	return available, nil
}
