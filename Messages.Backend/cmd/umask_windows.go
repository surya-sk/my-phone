//go:build windows

package cmd

func setPrivateUmask() func() { return func() {} }
