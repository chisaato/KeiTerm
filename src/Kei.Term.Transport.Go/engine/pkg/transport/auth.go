// Package transport provides platform-independent SSH and SFTP transports.
package transport

import (
	"errors"
	"fmt"

	"golang.org/x/crypto/ssh"
)

// SignerSource may load agent or hardware-backed signers lazily. The caller owns
// the agent connection and must keep it alive for the duration of authentication.
type SignerSource func() ([]ssh.Signer, error)

// Auth configures authentication without persisting or logging secret material.
// Password is a pointer so an intentionally empty password remains supported.
type Auth struct {
	Password   *string
	PrivateKey []byte
	Passphrase []byte
	Signers    SignerSource
	// Additional supports keyboard-interactive and future authentication methods.
	Additional []ssh.AuthMethod
}

func (a Auth) Methods() ([]ssh.AuthMethod, error) {
	methods := make([]ssh.AuthMethod, 0, 3+len(a.Additional))
	if a.Password != nil {
		methods = append(methods, ssh.Password(*a.Password))
	}
	var localSigners []ssh.Signer
	if len(a.PrivateKey) > 0 {
		var signer ssh.Signer
		var err error
		if len(a.Passphrase) > 0 {
			signer, err = ssh.ParsePrivateKeyWithPassphrase(a.PrivateKey, a.Passphrase)
		} else {
			signer, err = ssh.ParsePrivateKey(a.PrivateKey)
		}
		if err != nil {
			return nil, fmt.Errorf("parse private key: %w", err)
		}
		localSigners = append(localSigners, signer)
	}
	if len(localSigners) > 0 || a.Signers != nil {
		// SSH attempts each method name once. Combine all public keys into one
		// method so an unaccepted local key does not hide valid agent keys.
		methods = append(methods, ssh.PublicKeysCallback(func() ([]ssh.Signer, error) {
			signers := append([]ssh.Signer(nil), localSigners...)
			if a.Signers != nil {
				external, err := a.Signers()
				if err != nil {
					return nil, err
				}
				for _, signer := range external {
					if signer == nil {
						return nil, errors.New("nil external signer")
					}
				}
				signers = append(signers, external...)
			}
			return signers, nil
		}))
	}
	for _, method := range a.Additional {
		if method == nil {
			return nil, errors.New("nil authentication method")
		}
		methods = append(methods, method)
	}
	if len(methods) == 0 {
		return nil, errors.New("at least one authentication method is required")
	}
	return methods, nil
}
