package main

import (
	"fmt"
	"github.com/GameXG/ProxyClient"
	"github.com/GameXG/TcpRoute2/netchan"
	"log"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

/*

需要建立多个连接，是用最快建立的连接。


*/

type DialClient struct {
	name         string
	dnsResolve   bool
	pc           proxyclient.ProxyClient
	dialCredit   int           // 线路可靠(信誉)程度
	sleep        time.Duration // 线路在使用前等待的时间
	correctDelay time.Duration // 对 tcping 修正
	whitelist    []*domainRuleSource
	blacklist    []*domainRuleSource
}

// TcpPing 方式选择最快连接上的。
type tcppingUpStream struct {
	errConn   *ErrConnService
	dc        []DialClient
	connCache *upStreamConnCache
	hosts     []*hostRuleSource
}

type chanDialTimeoutUserData struct {
	dialName   string
	domainAddr string // connChan 有 domainAddr 还增加这个字段的原因是使用缓存时， domainAddr 填入的是缓存的ip
	dialClient *DialClient
}

func NewTcppingUpStream() *tcppingUpStream {
	errConn := NewErrConnService()
	connCache := NewUpStreamConnCache(errConn)

	tUpstream := tcppingUpStream{
		errConn:   errConn,
		dc:        make([]DialClient, 0, 5),
		connCache: connCache,
		hosts:     nil,
	}

	return &tUpstream
}

func (su *tcppingUpStream) SetHostSources(configs []ServerConfigHostSource, baseDir string) error {
	hosts, err := loadHostRuleSources(configs, baseDir)
	if err != nil {
		return fmt.Errorf("load hosts: %w", err)
	}
	su.hosts = hosts
	netchan.SetHostsResolver(func(domain string) []netchan.DnsRecord {
		return su.lookupHostRecords(domain)
	})
	return nil
}

type dialTimeoutRes struct {
	conn         net.Conn
	errReporting UpStreamErrorReporting
	err          error
}

func (su *tcppingUpStream) AddUpStream(name, proxyUrl string, dnsResolve bool, credit int, sleep time.Duration, correctDelay time.Duration) error {
	// 加入线路
	pc, err := proxyclient.NewProxyClient(proxyUrl)
	if err != nil {
		return fmt.Errorf("无法创建上级代理：%v", err)
	}
	pc, err = bindUpstream(pc)
	if err != nil {
		return fmt.Errorf("bind upstream to physical interface: %w", err)
	}

	dialCredit := credit
	creditQuery, ok := pc.GetProxyAddrQuery()["credit"]
	if ok {
		if len(creditQuery) > 1 {
			return fmt.Errorf("代理 credit 重复设置，代理url:%v", proxyUrl)

		}
		dialCreditTem, err := strconv.Atoi(creditQuery[0])
		if err == nil {
			dialCredit = dialCreditTem
		}
	}

	su.dc = append(su.dc, DialClient{
		name:         name,
		dnsResolve:   dnsResolve,
		pc:           pc,
		dialCredit:   dialCredit,
		sleep:        sleep,
		correctDelay: correctDelay,
	})
	return nil
}

func (su *tcppingUpStream) AddConfiguredUpStream(config ServerConfigUpStream, baseDir string) error {
	name := strings.TrimSpace(config.Name)
	for _, existing := range su.dc {
		if existing.name == name {
			return fmt.Errorf("duplicate upstream name: %s", name)
		}
	}
	whitelist, err := loadDomainRuleSources(config.Whitelist, baseDir)
	if err != nil {
		return fmt.Errorf("load upstream %q whitelist: %w", name, err)
	}
	blacklist, err := loadDomainRuleSources(config.Blacklist, baseDir)
	if err != nil {
		return fmt.Errorf("load upstream %q blacklist: %w", name, err)
	}
	if err := su.AddUpStream(name, config.ProxyUrl, config.DnsResolve, config.Credit, time.Duration(config.Sleep)*time.Millisecond, time.Duration(config.CorrectDelay)*time.Millisecond); err != nil {
		return err
	}
	su.dc[len(su.dc)-1].whitelist = whitelist
	su.dc[len(su.dc)-1].blacklist = blacklist
	return nil
}

func (su *tcppingUpStream) DialTimeout(network, address string, timeout time.Duration) (net.Conn, UpStreamErrorReporting, error) {
	dialClients, manuallyRouted := su.selectDialClients(address)
	if len(dialClients) == 0 {
		return nil, nil, fmt.Errorf("no upstream is eligible for %s", address)
	}
	if strings.HasPrefix(network, "udp") {
		return su.dialUDP(network, address, timeout, dialClients)
	}
	// 尝试使用缓存中的连接
	item, err := su.connCache.GetOptimal(address)
	// Re-resolve local routes with hosts sources so edits/removals and Credit
	// changes cannot be bypassed by a previously cached DNS or hosts address.
	cacheEligible := false
	if err == nil {
		for _, client := range dialClients {
			if client.name == item.dialName {
				cacheEligible = true
				break
			}
		}
	}
	if err == nil && cacheEligible && (len(su.hosts) == 0 || !item.dialClient.dnsResolve) {

		ctimeout := item.TcpPing
		if ctimeout < 100*time.Millisecond {
			ctimeout += 20 * time.Millisecond
		} else if ctimeout < 500*time.Millisecond {
			ctimeout += 50 * time.Millisecond
		} else {
			ctimeout += 100 * time.Millisecond
		}

		// 考虑了下，还是使用原始的连接方式，而没有使用 dialChan
		n := time.Now()
		c, err := item.dialClient.pc.DialTimeout(network, item.IpAddr, ctimeout)
		if err == nil {
			fmt.Printf("缓存命中：%v 代理：%v IP：%v \r\n", address, item.dialName, item.IpAddr)
			go su.connCache.Updata(address, item.IpAddr, time.Now().Sub(n)+item.dialClient.correctDelay, item.dialClient, item.dialName)
			ErrorReporting := &UpStreamErrorReportingBase{su.errConn, item.dialName, item.DomainAddr, item.IpAddr}
			return c, ErrorReporting, nil
		} else {
			fmt.Printf("缓存连接失败：%v 代理：%v IP：%v err:%v \r\n", address, item.dialName, item.IpAddr, err)
		}
	}

	// 缓存未命中(缓存故障)时删除缓存记录
	su.connCache.Del(address)

	// 缓存未命中时同时使用多个线路尝试连接。
	resChan := make(chan dialTimeoutRes)
	connChan := make(chan netchan.ConnRes, 10)
	exitChan := make(chan int)
	// 选定连接 退出函数时不再尝试新的连接
	defer func() {
		defer func() { _ = recover() }()
		close(exitChan)
	}()

	// 另开一个线程进行连接并整理连接信息
	go func() {
		// 循环使用各个 upstream 进行连接
		sw := sync.WaitGroup{}
		sw.Add(len(dialClients))
		for _, d := range dialClients {
			d := d
			go func() {
				defer func() { sw.Done() }()

				// Explicit whitelist/blacklist routing ignores racing delay.
				// 显式黑白名单分流不再应用竞速延迟。
				if !manuallyRouted {
					time.Sleep(d.sleep)
				}
				select {
				case <-exitChan:
					return
				default:
				}

				userData := chanDialTimeoutUserData{d.name, address, &d}
				cerr := netchan.ChanDialTimeout(d.pc, d.dialCredit, connChan, exitChan, d.dnsResolve, &userData, nil, network, address, timeout)
				if cerr != nil {
					log.Printf("线路 %v 连接 %v 失败，错误：%v", d.name, address, cerr)
				}
			}()
		}

		// 所有连接线程都结束时关闭 connChan 信道
		// 终止取结果的线程，防止永久阻塞。
		go func() {
			sw.Wait()
			close(connChan)
		}()

		// 取结果
		// 将最快建立的结果返回给 resChan 好返回主函数。
		// 在无法建立连接时将返回err
		go func() {
			ok := false         // 是否已经找到最快的稳定连接
			var oConn net.Conn  // 保存最快的结果，如果全部的连接都有问题时间使用这个连接
			oConnClose := false //oConn 是否需要关闭。
			oConnCloseMutex := sync.Mutex{}
			var ErrorReporting *UpStreamErrorReportingBase //错误报告（实际使用 oconn 连接如果发现问题通过本变量报告错误）
			var oconnTimeout *time.Timer                   // oconn 定时器

			defer func() {
				// 结束时函数关闭之前保存的最快的连接。
				oConnCloseMutex.Lock()
				defer oConnCloseMutex.Unlock()
				if oConnClose == true {
					oConn.Close()
				}
			}()

			for conn := range connChan {
				// 保存最快的连接（可能并不稳定） 用于应付找不到稳定连接的情况
				conn := conn
				userData := conn.UserData.(*chanDialTimeoutUserData)

				// 上报这个连接建立的速度
				go su.connCache.Updata(userData.domainAddr, conn.IpAddr, conn.Ping+userData.dialClient.correctDelay, userData.dialClient, userData.dialName)

				// 返回最快并稳定的连接
				if ok == false && su.errConn.Check(userData.dialName, userData.domainAddr, conn.IpAddr) == true {
					// 找到了最快的稳定连接
					ok = true
					ErrorReporting = &UpStreamErrorReportingBase{su.errConn, userData.dialName, userData.domainAddr, conn.IpAddr}
					if oconnTimeout != nil {
						oconnTimeout.Stop()
					}
					func() {
						defer func() { _ = recover() }()
						resChan <- dialTimeoutRes{conn.Conn, ErrorReporting, nil}
					}()
					log.Printf("为 %v 找到了最快的稳定连接 %v ，线路：%v.\r\n", userData.domainAddr, conn.IpAddr, userData.dialName)
				} else {
					// 已经有最快稳定链接 或者 本连接不稳定。

					// 未安全返回时保存最快的一个连接
					if oConn == nil && ok == false {
						// 如果未找到可靠连接并且本连接时最快建立的连接 就先保存下本连接等待备用。
						oConn = conn.Conn
						oConnClose = true
						ErrorReporting = &UpStreamErrorReportingBase{su.errConn, userData.dialName, userData.domainAddr, conn.IpAddr}

						// 如果一定时间内还没找到最快的稳定线路那么就是用这个线路，并且不再尝试新连接。
						oconnTimeout = time.AfterFunc(1*time.Second, func() {
							defer func() { _ = recover() }()
							oConnCloseMutex.Lock()
							defer oConnCloseMutex.Unlock()
							resChan <- dialTimeoutRes{oConn, ErrorReporting, nil}
							oConnClose = false
						})

					} else {
						conn.Conn.Close()
					}
				}
			}

			// 最后如果连接未建立，并且有最快建立的连接，那么即使他不稳定也是用这个。
			if ok == false && oConn != nil {
				func() {
					defer func() { recover() }()
					oConnCloseMutex.Lock()
					defer oConnCloseMutex.Unlock()
					resChan <- dialTimeoutRes{oConn, ErrorReporting, nil}
					oConnClose = false
					log.Printf("为 %v 找到了最快但不稳定连接 %v ，线路：%v.\r\n", ErrorReporting.DomainAddr, ErrorReporting.IpAddr, ErrorReporting.DailName)
				}()
				return
			}

			// 最后还是没找到可用连接
			if ok == false {
				func() {
					defer func() { recover() }()
					resChan <- dialTimeoutRes{nil, nil, fmt.Errorf("所有线路建立连接失败。")}
				}()
			}
		}()
	}()

	res := <-resChan
	close(resChan)
	return res.conn, res.errReporting, res.err
}

func (su *tcppingUpStream) dialUDP(network, address string, timeout time.Duration, clients []DialClient) (net.Conn, UpStreamErrorReporting, error) {
	var lastErr error
	for _, client := range clients {
		result := make(chan struct {
			conn net.Conn
			err  error
		}, 1)
		go func(client DialClient) {
			conn, err := client.pc.Dial(network, address)
			result <- struct {
				conn net.Conn
				err  error
			}{conn, err}
		}(client)

		select {
		case outcome := <-result:
			if outcome.err == nil {
				log.Printf("为 %v 建立 UDP 线路：%v.", address, client.name)
				return outcome.conn, &UpStreamErrorReportingBase{su.errConn, client.name, address, address}, nil
			}
			lastErr = outcome.err
		case <-time.After(timeout):
			lastErr = fmt.Errorf("UDP 线路 %s 连接超时", client.name)
			go func() {
				outcome := <-result
				if outcome.conn != nil {
					outcome.conn.Close()
				}
			}()
		}
	}
	return nil, nil, fmt.Errorf("所有 UDP 线路建立失败: %w", lastErr)
}
