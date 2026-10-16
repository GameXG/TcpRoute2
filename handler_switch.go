package main

import (
	"fmt"
	"github.com/gamexg/preRead"
	"net"
)

// handler 切换器
// 会尝试使用各种类型的 Handler。
type switchHandlerNewer struct {
	handlerNewers []HandlerNewer // 子处理器列表
}

func NewSwitchHandlerNewer() *switchHandlerNewer {
	return &switchHandlerNewer{make([]HandlerNewer, 0, 10)}
}

func (sh *switchHandlerNewer) AppendHandlerNewer(h HandlerNewer) {
	sh.handlerNewers = append(sh.handlerNewers, h)
}

// 尝试创建处理
// 这里会循环尝试创建所有的处理器
func (sh *switchHandlerNewer) New(conn net.Conn) (h Handler, rePre bool, err error) {
	pc := preread.NewPreConn(conn)
	pc.NewPre()
	defer pc.ClosePre()

	// 预先读一次数据
	b := make([]byte, 4096)
	if n, err := pc.Read(b); err != nil {
		b = b[0:0]
	} else {
		b = b[:n]
	}
	pc.ResetPreOffset()
	if len(b) > 0 && b[0] == 0x04 {
		return nil, true, NoHandleError("收到 SOCKS4 请求；TcpRoute2 监听端口仅支持 SOCKS5，请将客户端代理协议改为 SOCKS5")
	}

	for _, hn := range sh.handlerNewers {
		if h, reset, _ := hn.New(pc); h != nil {
			if reset {
				pc.ResetPreOffset()
			}
			return h, false, nil
		} else {
			pc.ResetPreOffset()
		}
	}

	return nil, true, NoHandleError(fmt.Sprintf("无法识别的协议：%v", b[:min(len(b), 10)]))
}
