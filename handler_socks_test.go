package main

import (
	"io"
	"net"
	"testing"
	"time"
)

type directTCPTestUpstream struct{}

func (directTCPTestUpstream) DialTimeout(network, address string, timeout time.Duration) (net.Conn, UpStreamErrorReporting, error) {
	conn, err := net.DialTimeout(network, address, timeout)
	return conn, nil, err
}

func TestSocks4AndSocks4aConnect(t *testing.T) {
	for _, test := range []struct {
		name   string
		prefix []byte
	}{
		{name: "socks4", prefix: []byte{0x00, 0x00, 0x00, 0x00}},
		{name: "socks4a", prefix: []byte{0x00, 0x00, 0x00, 0x01}},
	} {
		t.Run(test.name, func(t *testing.T) {
			target, err := net.Listen("tcp", "127.0.0.1:0")
			if err != nil {
				t.Fatal(err)
			}
			defer target.Close()
			port := target.Addr().(*net.TCPAddr).Port
			serverDone := make(chan error, 1)
			go func() {
				conn, acceptErr := target.Accept()
				if acceptErr != nil {
					serverDone <- acceptErr
					return
				}
				defer conn.Close()
				if _, err := io.Copy(conn, conn); err != nil {
					serverDone <- err
				}
			}()

			client, server := net.Pipe()
			handler := &hSocksHandle{hSocksServer: &hSocksServer{upStream: directTCPTestUpstream{}}, conn: server}
			handlerDone := make(chan error, 1)
			go func() { handlerDone <- handler.Handle() }()

			request := []byte{0x04, 0x01, byte(port >> 8), byte(port)}
			request = append(request, test.prefix...)
			request = append(request, 0x00)
			if test.name == "socks4a" {
				request = append(request, []byte("127.0.0.1")...)
				request = append(request, 0x00)
			}
			if _, err := client.Write(request); err != nil {
				t.Fatal(err)
			}
			reply := make([]byte, 8)
			if _, err := io.ReadFull(client, reply); err != nil {
				t.Fatal(err)
			}
			if reply[0] != 0 || reply[1] != 0x5a {
				t.Fatalf("SOCKS4 reply = %v", reply)
			}
			if _, err := client.Write([]byte("ping")); err != nil {
				t.Fatal(err)
			}
			response := make([]byte, 4)
			if _, err := io.ReadFull(client, response); err != nil {
				t.Fatal(err)
			}
			if string(response) != "ping" {
				t.Fatalf("response = %q", response)
			}
			client.Close()
			if err := <-handlerDone; err != nil && err != io.EOF {
				t.Fatalf("handler error: %v", err)
			}
		})
	}
}
