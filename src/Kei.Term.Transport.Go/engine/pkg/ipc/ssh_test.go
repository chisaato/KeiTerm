package ipc

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"golang.org/x/crypto/ssh"
)

func sshPeer(t *testing.T) (ConnectParams, *atomic.Int32, <-chan struct{}) {
	t.Helper()
	_, key, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := ssh.NewSignerFromKey(key)
	if err != nil {
		t.Fatal(err)
	}
	var authenticated atomic.Int32
	heartbeat := make(chan struct{}, 8)
	config := &ssh.ServerConfig{PasswordCallback: func(_ ssh.ConnMetadata, password []byte) (*ssh.Permissions, error) {
		authenticated.Add(1)
		if string(password) != "secret" {
			return nil, errors.New("bad password")
		}
		return nil, nil
	}}
	config.AddHostKey(signer)
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var workers sync.WaitGroup
	var connections sync.Map
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			connections.Store(conn, true)
			workers.Add(1)
			go func() {
				defer workers.Done()
				defer connections.Delete(conn)
				defer conn.Close()
				server, channels, requests, err := ssh.NewServerConn(conn, config)
				if err != nil {
					return
				}
				defer server.Close()
				go func() {
					for channel := range channels {
						_ = channel.Reject(ssh.UnknownChannelType, "unused")
					}
				}()
				for request := range requests {
					if request.Type == "keepalive@openssh.com" {
						select {
						case heartbeat <- struct{}{}:
						default:
						}
					}
					_ = request.Reply(false, nil)
				}
			}()
		}
	}()
	t.Cleanup(func() {
		_ = listener.Close()
		<-done
		connections.Range(func(conn, _ any) bool { _ = conn.(net.Conn).Close(); return true })
		workers.Wait()
	})
	host, port, _ := net.SplitHostPort(listener.Addr().String())
	portNumber, _ := strconv.Atoi(port)
	password := "secret"
	return ConnectParams{Endpoint: Endpoint{Host: host, Port: portNumber, User: "test", Auth: Auth{Password: &password}, HostKeySHA256: ssh.FingerprintSHA256(signer.PublicKey())}}, &authenticated, heartbeat
}

func TestHostKeyProbeStopsBeforeAuthenticationAndPinsReconnect(t *testing.T) {
	p, authentications, _ := sshPeer(t)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	var key presentedKey
	_, err := dialSSH(ctx, p, &key)
	if !errors.Is(err, errKeyProbed) || key.Fingerprint != p.HostKeySHA256 || key.PublicKeyBase64 == "" || key.KeyType != "ssh-ed25519" {
		t.Fatalf("probe: %+v, %v", key, err)
	}
	if authentications.Load() != 0 {
		t.Fatal("probe sent target credentials")
	}
	session, err := connectSSH(ctx, p)
	if err != nil {
		t.Fatal(err)
	}
	_ = session.Close()
	p.HostKeySHA256 = "SHA256:wrong"
	p.InsecureIgnoreHostKey = true
	if _, err = connectSSH(ctx, p); err == nil {
		t.Fatal("explicit insecure flag bypassed pinned fingerprint")
	}
	p.HostKeySHA256 = ""
	p.InsecureIgnoreHostKey = false
	if _, err = connectSSH(ctx, p); err == nil {
		t.Fatal("missing fingerprint silently accepted")
	}
	p.InsecureIgnoreHostKey = true
	session, err = connectSSH(ctx, p)
	if err != nil {
		t.Fatal(err)
	}
	_ = session.Close()
}

func TestKeepAliveEndsWithSession(t *testing.T) {
	p, _, heartbeat := sshPeer(t)
	p.KeepAliveSeconds = 1
	session, err := connectSSH(context.Background(), p)
	if err != nil {
		t.Fatal(err)
	}
	defer session.Close()
	select {
	case <-heartbeat:
	case <-time.After(3 * time.Second):
		t.Fatal("no keepalive request")
	}
	_ = session.Close()
	select {
	case <-heartbeat:
		t.Fatal("keepalive continued after close")
	case <-time.After(1100 * time.Millisecond):
	}
}
