package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"strings"
	"time"

	"github.com/GameXG/TcpRoute2/netchan"
	"github.com/miekg/dns"
	"github.com/xtls/xray-core/common"
	"github.com/xtls/xray-core/common/buf"
	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/net/cnc"
	"github.com/xtls/xray-core/features/routing"
	"github.com/xtls/xray-core/transport"
)

type tunDispatcher struct {
	upstream   UpStreamDial
	bypass     func(context.Context, xnet.Destination) (bool, error)
	directDial func(context.Context, string, string) (net.Conn, error)
}

func (*tunDispatcher) Type() interface{} { return routing.DispatcherType() }
func (*tunDispatcher) Start() error      { return nil }
func (*tunDispatcher) Close() error      { return nil }
func (*tunDispatcher) Dispatch(context.Context, xnet.Destination) (*transport.Link, error) {
	return nil, fmt.Errorf("TUN requires DispatchLink")
}

func (d *tunDispatcher) DispatchLink(ctx context.Context, destination xnet.Destination, link *transport.Link) error {
	defer common.Interrupt(link.Reader)
	defer common.Close(link.Writer)
	address := destination.NetAddr()
	bypass := false
	if d.bypass != nil {
		var err error
		bypass, err = d.bypass(ctx, destination)
		if err != nil {
			return err
		}
	}
	if !bypass && destination.Address.Family().IsIPv6() && !environmentEnabled("TCPROUTE_ENABLE_IPV6") {
		return fmt.Errorf("IPv6 is disabled")
	}
	if destination.Network == xnet.Network_UDP {
		return d.forwardUDP(ctx, address, link, bypass)
	}
	if destination.Network != xnet.Network_TCP {
		return fmt.Errorf("unsupported TUN network")
	}
	input := cnc.NewConnection(cnc.ConnectionOutputMulti(link.Reader), cnc.ConnectionInputMulti(link.Writer))
	defer input.Close()
	if destination.Port.Value() == 53 && (!bypass || isTunDNSGateway(address)) {
		return forwardTunTCPDNS(ctx, input)
	}
	if protocol := CheckPre("tcp", address); protocol != 0 && !bypass {
		original := input
		timer := time.AfterFunc(10*time.Second, func() { _ = original.Close() })
		input, address, _ = Pre(input, address, protocol)
		timer.Stop()
		if input == nil {
			return fmt.Errorf("could not inspect TUN destination")
		}
	}
	output, err := d.dial(ctx, "tcp", address, bypass)
	if err != nil {
		return err
	}
	defer output.Close()
	finished := make(chan error, 2)
	go func() { _, err := io.Copy(output, input); finished <- err }()
	go func() { _, err := io.Copy(input, output); finished <- err }()
	select {
	case err := <-finished:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (d *tunDispatcher) dial(ctx context.Context, network, address string, bypass bool) (net.Conn, error) {
	if bypass {
		if d.directDial != nil {
			return d.directDial(ctx, network, address)
		}
		return netchan.OutboundDialer(15*time.Second).DialContext(ctx, network, address)
	}
	conn, _, err := d.upstream.DialTimeout(network, address, 15*time.Second)
	return conn, err
}

func isTunDNSGateway(address string) bool {
	host, port, _ := net.SplitHostPort(address)
	return port == "53" && (host == "198.18.0.1" || host == "fd73:7463:7072::1")
}

func (d *tunDispatcher) forwardUDP(ctx context.Context, address string, link *transport.Link, bypass bool) error {
	first, err := link.Reader.ReadMultiBuffer()
	if err != nil {
		return err
	}
	_, port, _ := net.SplitHostPort(address)
	if port == "53" && (!bypass || isTunDNSGateway(address)) {
		for {
			for _, packet := range first {
				answer := tunDNSResponse(ctx, packet.Bytes())
				if answer != nil {
					if err := link.Writer.WriteMultiBuffer(buf.MultiBuffer{buf.FromBytes(answer)}); err != nil {
						buf.ReleaseMulti(first)
						return err
					}
				}
			}
			buf.ReleaseMulti(first)
			first, err = link.Reader.ReadMultiBuffer()
			if err != nil {
				buf.ReleaseMulti(first)
				return err
			}
		}
	}
	if len(first) == 0 {
		return io.EOF
	}
	if !bypass && !udpDestinationAllowed(address, first[0].Bytes()) {
		buf.ReleaseMulti(first)
		return fmt.Errorf("UDP destination is disabled")
	}
	output, err := d.dial(ctx, "udp", address, bypass)
	if err != nil {
		buf.ReleaseMulti(first)
		return err
	}
	defer output.Close()
	finished := make(chan error, 2)
	go func() {
		packets := first
		for {
			for _, packet := range packets {
				if !bypass && !udpDestinationAllowed(address, packet.Bytes()) {
					continue
				}
				_ = output.SetWriteDeadline(time.Now().Add(30 * time.Second))
				if _, err := output.Write(packet.Bytes()); err != nil {
					buf.ReleaseMulti(packets)
					finished <- err
					return
				}
			}
			buf.ReleaseMulti(packets)
			var err error
			packets, err = link.Reader.ReadMultiBuffer()
			if err != nil {
				buf.ReleaseMulti(packets)
				finished <- err
				return
			}
		}
	}()
	go func() {
		packet := make([]byte, 65535)
		for {
			_ = output.SetReadDeadline(time.Now().Add(60 * time.Second))
			n, err := output.Read(packet)
			if err != nil {
				finished <- err
				return
			}
			answer := buf.NewWithSize(int32(n))
			_, _ = answer.Write(packet[:n])
			if err := link.Writer.WriteMultiBuffer(buf.MultiBuffer{answer}); err != nil {
				finished <- err
				return
			}
		}
	}()
	select {
	case err := <-finished:
		return err
	case <-ctx.Done():
		return ctx.Err()
	}
}

func tunDNSResponse(ctx context.Context, wire []byte) []byte {
	query := new(dns.Msg)
	if query.Unpack(wire) != nil || query.Response || len(query.Question) != 1 {
		return nil
	}
	answer := new(dns.Msg)
	answer.SetReply(query)
	answer.RecursionAvailable = true
	question := query.Question[0]
	if question.Qclass != dns.ClassINET || (question.Qtype != dns.TypeA && question.Qtype != dns.TypeAAAA) {
		answer.Rcode = dns.RcodeNotImplemented
	} else if question.Qtype != dns.TypeAAAA || environmentEnabled("TCPROUTE_ENABLE_IPV6") {
		request, cancel := context.WithTimeout(ctx, 10*time.Second)
		ips, err := netchan.LookupDNS(request, strings.TrimSuffix(question.Name, "."))
		cancel()
		if err != nil {
			answer.Rcode = dns.RcodeServerFailure
			if negative, ok := err.(*net.DNSError); ok && negative.IsNotFound {
				answer.Rcode = dns.RcodeNameError
			}
		}
		for _, ip := range ips {
			header := dns.RR_Header{Name: question.Name, Rrtype: question.Qtype, Class: dns.ClassINET, Ttl: 60}
			if question.Qtype == dns.TypeA && ip.To4() != nil {
				answer.Answer = append(answer.Answer, &dns.A{Hdr: header, A: ip.To4()})
			}
			if question.Qtype == dns.TypeAAAA && ip.To4() == nil {
				answer.Answer = append(answer.Answer, &dns.AAAA{Hdr: header, AAAA: ip})
			}
		}
	}
	result, _ := answer.Pack()
	return result
}

func forwardTunTCPDNS(ctx context.Context, conn net.Conn) error {
	var size [2]byte
	for {
		_ = conn.SetDeadline(time.Now().Add(60 * time.Second))
		if _, err := io.ReadFull(conn, size[:]); err != nil {
			return err
		}
		payload := make([]byte, binary.BigEndian.Uint16(size[:]))
		if _, err := io.ReadFull(conn, payload); err != nil {
			return err
		}
		response := tunDNSResponse(ctx, payload)
		if response == nil {
			return fmt.Errorf("invalid TUN DNS request")
		}
		binary.BigEndian.PutUint16(size[:], uint16(len(response)))
		if _, err := conn.Write(append(size[:], response...)); err != nil {
			return err
		}
	}
}
