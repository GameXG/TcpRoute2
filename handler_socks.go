package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"net"
	"strconv"
	"sync/atomic"
	"time"
)

const forwardBufSize = 8192 // 转发缓冲区大小

// socks 服务器
type hSocksServer struct {
	upStream UpStreamDial
}

type hSocksHandle struct {
	hSocksServer *hSocksServer
	conn         net.Conn
}

func NewSocksHandlerNewer(upStream UpStreamDial) HandlerNewer {
	return &hSocksServer{upStream}
}

// 尝试创建 socks 处理器
func (sev *hSocksServer) New(conn net.Conn) (h Handler, rePre bool, err error) {
	// 读取前几个字节，判断是否为 socks 代理协议。
	// 是则返回 handler
	rePre = true
	b := make([]byte, 1024)

	if n, cerr := conn.Read(b); cerr != nil {
		err = fmt.Errorf("读取错误：%v", cerr)
		fmt.Println(err)
		fmt.Println(b[:100])
		return
	} else {
		b = b[:n]
	}
	if len(b) == 0 {
		return nil, true, io.EOF
	}

	// SOCKS4/4a and SOCKS5 share this listener. The complete request is
	// parsed by Handle after the pre-read buffer is rewound by the switcher.
	if b[0] == 0x04 || b[0] == 0x05 {
		return &hSocksHandle{sev, conn}, true, nil
	} else {
		err = NoHandleError("协议头不正确。")
		return
	}
}

func (h *hSocksHandle) String() string {
	return "Socks4/4a/5"
}

// 实际处理 Socks 协议
func (h *hSocksHandle) Handle() error {
	// 鉴定 + 接受 CMD 的总允许时间
	h.conn.SetDeadline(time.Now().Add(handlerTimeoutHello))

	b := make([]byte, 1)
	if n, err := h.conn.Read(b); err != nil || n != 1 {
		return fmt.Errorf("协议头读取错误：%v", err)
	}

	if b[0] == 0x05 {
		return h.handleSocks5()
	} else if b[0] == 0x04 {
		return h.handleSocks4()
	} else {
		return NoHandleError("无法识别的协议。")
	}
}

func (h *hSocksHandle) handleSocks4() error {
	conn := h.conn
	request := make([]byte, 7)
	if _, err := io.ReadFull(conn, request); err != nil {
		return fmt.Errorf("读取 SOCKS4 请求失败：%v", err)
	}
	cmd := request[0]
	port := binary.BigEndian.Uint16(request[1:3])
	ip := net.IP(request[3:7])
	if _, err := readSocks4String(conn); err != nil {
		return fmt.Errorf("读取 SOCKS4 用户标识失败：%v", err)
	}

	host := ip.String()
	if request[3] == 0 && request[4] == 0 && request[5] == 0 && request[6] != 0 {
		var err error
		host, err = readSocks4String(conn)
		if err != nil {
			return fmt.Errorf("读取 SOCKS4a 域名失败：%v", err)
		}
		if host == "" {
			return fmt.Errorf("SOCKS4a 域名为空")
		}
	}
	if cmd != 0x01 {
		_ = writeSocks4Reply(conn, 0x5b, nil, 0)
		return fmt.Errorf("不支持的 SOCKS4 命令：%v", cmd)
	}
	return h.handleRoutedConnect(host, port, 4)
}

func readSocks4String(conn io.Reader) (string, error) {
	value := make([]byte, 0, 32)
	var one [1]byte
	for len(value) <= 255 {
		if _, err := io.ReadFull(conn, one[:]); err != nil {
			return "", err
		}
		if one[0] == 0 {
			return string(value), nil
		}
		value = append(value, one[0])
	}
	return "", fmt.Errorf("SOCKS4 字段过长")
}

func writeSocks4Reply(conn io.Writer, code byte, address net.IP, port uint16) error {
	var ip4 net.IP
	if address != nil {
		ip4 = address.To4()
	}
	if ip4 == nil {
		ip4 = net.IPv4zero.To4()
	}
	reply := []byte{0x00, code, byte(port >> 8), byte(port), ip4[0], ip4[1], ip4[2], ip4[3]}
	_, err := conn.Write(reply)
	return err
}

func (h *hSocksHandle) handleSocks5() error {
	conn := h.conn //客户端连接

	b := make([]byte, 1, 1024)

	// 读客户端支持的鉴定方法数量
	if n, err := conn.Read(b); err != nil || n != 1 {
		return fmt.Errorf("协议头读取错误：%v", err)
	}
	nmethods := b[0] // 客户端支持的鉴定方法数量

	// 读鉴定方式
	b = b[:nmethods]
	if _, err := io.ReadFull(conn, b); err != nil {
		return fmt.Errorf("读客户端支持的鉴定方法失败：%v", err)
	}

	//判断是否存在无需鉴定
	if bytes.Contains(b, []byte{0}) != true {
		return fmt.Errorf("客户端不支持无鉴定，登陆失败。客户端支持的鉴定方式：%v", b)
	}

	// 回应鉴定请求
	if _, err := conn.Write([]byte{0x05, 0}); err != nil {
		return fmt.Errorf("回应鉴定错误：%v", err)
	}

	b = b[:5]
	if _, err := io.ReadFull(conn, b); err != nil {
		return fmt.Errorf("读请求错误：%v", err)
	}
	ver := b[0]
	cmd := b[1]
	//rsv := b[2]
	atyp := b[3]
	domainSize := b[4]
	host := ""
	prot := uint16(0)

	// 读地址
	if atyp == 0x01 || atyp == 0x04 {
		//IPv4 or IPv6
		if atyp == 0x01 {
			b = b[:4]
		} else {
			b = b[:16]
		}
		b[0] = domainSize

		if _, err := io.ReadFull(conn, b[1:]); err != nil {
			return fmt.Errorf("读地址错误：%v", err)
		}
		host = net.IP(b).String()
	} else if atyp == 0x03 {
		b = b[:domainSize]
		if _, err := io.ReadFull(conn, b); err != nil {
			return fmt.Errorf("读地址错误：%v", err)
		}
		host = string(b)
	} else {
		return fmt.Errorf("不支持的地址格式：atyp=%v", atyp)
	}

	// 读端口
	b = b[:2]
	if _, err := io.ReadFull(conn, b); err != nil {
		return fmt.Errorf("读端口错误：%v", err)
	}
	prot = binary.BigEndian.Uint16(b)

	if ver != 0x05 {
		conn.Write([]byte{0x05, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00})
		return fmt.Errorf("不支持的 SOCKS 版本。ver=%v", ver)
	}
	if cmd == 0x03 {
		return h.handleSocks5UDPAssociate(conn)
	}
	if cmd != 0x01 {
		conn.Write([]byte{0x05, 0x07, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00})
		return fmt.Errorf("不支持的命令。ver=%v,cmd=%v", ver, cmd)
	}
	return h.handleRoutedConnect(host, prot, 5)
}

func (h *hSocksHandle) handleRoutedConnect(host string, prot uint16, version byte) error {
	conn := h.conn
	replySuccess := func() error {
		if version == 4 {
			return writeSocks4Reply(conn, 0x5a, nil, 0)
		}
		_, err := conn.Write([]byte{0x05, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00})
		return err
	}
	replyFailure := func() {
		if version == 4 {
			_ = writeSocks4Reply(conn, 0x5b, nil, 0)
			return
		}
		_, _ = conn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00})
	}

	if environmentEnabled("TCPROUTE_TUN_DNS") && host == "198.18.0.1" && prot == 53 {
		if err := replySuccess(); err != nil {
			return err
		}
		return forwardTunTCPDNS(context.Background(), conn)
	}

	conn.SetDeadline(time.Now().Add(handlerTimeoutForward))
	rAddr := net.JoinHostPort(host, strconv.FormatUint(uint64(prot), 10))
	preProtocol := CheckPre("tcp", rAddr)
	if preProtocol != preProtocolUnknown {
		if err := replySuccess(); err != nil {
			return fmt.Errorf("回应客户端命令失败：%v", err)
		}
		nextConn, nextAddress, ok := Pre(conn, rAddr, preProtocol)
		if ok {
			log.Printf("[客户端 DNS 纠正] 目的地址 %v 已恢复为 %v", rAddr, nextAddress)
		}
		conn = nextConn
		rAddr = nextAddress
	}

	oConn, oConnErrorReporting, err := h.hSocksServer.upStream.DialTimeout("tcp", rAddr, handlerNewTimeout)
	if err != nil {
		conn.SetDeadline(time.Now().Add(handlerTimeoutHello))
		if preProtocol == preProtocolUnknown {
			replyFailure()
		}
		return fmt.Errorf("无法连接目标网站( %v )，详细错误：%v", rAddr, err)
	}
	defer oConn.Close()

	if preProtocol == preProtocolUnknown {
		if err := replySuccess(); err != nil {
			return fmt.Errorf("回应客户端命令失败：%v", err)
		}
	}

	fCount := forwardCount{}
	startTime := time.Now()
	err = forwardConn(conn, oConn, handlerTimeoutForward, &fCount)
	endTime := time.Now()
	if oConnErrorReporting != nil {
		connTime := endTime.Sub(startTime)
		lsend := atomic.LoadUint64(&fCount.send)
		lrecv := atomic.LoadUint64(&fCount.recv)
		if connTime < 60*time.Second && lrecv == 0 && lsend > 50 {
			oConnErrorReporting.Report(ErrConnTypeRead0)
		}
		if connTime < 1*time.Second && lrecv < 1024 && prot == 443 && lsend > 50 {
			oConnErrorReporting.Report(ErrConnTypeRead0)
		}
	}
	return err
}
