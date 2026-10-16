// TcpRoute2 project main.go
package main

import (
	_ "embed"
	"fmt"
	"github.com/GameXG/TcpRoute2/netchan"
	"github.com/koding/multiconfig"
	"os"
	"path/filepath"
	"strings"
)

//go:embed VERSION
var version string

type ServerConfig struct {
	Addr          string `default:"127.0.0.1:7070"`
	PreHttpPorts  []int
	PreHttpsPorts []int
	Hosts         []ServerConfigHostSource
	UpStreams     []ServerConfigUpStream
	Dns           []netchan.DNSConfig
	GeoData       []ServerConfigGeoData
}

type ServerConfigGeoData struct {
	Type           string
	URL            string
	ChecksumURL    string
	UpdateInterval string `default:"0"`
}

type ServerConfigHostSource struct {
	Path           string
	Type           string `default:"base"`
	UpdateInterval string `default:"24h"`
	Credit         int    `default:"0"`
}

type ServerConfigUpStream struct {
	Name         string `default:"direct"`
	Alias        string
	ProxyUrl     string `default:"direct://0.0.0.0:0000"`
	DialProxyFor string
	Remove       bool
	DnsResolve   bool `default:"false"`
	Credit       int  `default:"0"`
	Sleep        int  `default:"0"`
	CorrectDelay int  `default:"0"`
	Whitelist    []ServerConfigDomainList
	Blacklist    []ServerConfigDomainList
}

type ServerConfigDomainList struct {
	Path           string
	Type           string `default:"base"`
	UpdateInterval string `default:"24h"`
}

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--subscription-toml" {
		os.Exit(runSubscriptionTOMLEditor())
	}
	configPath, printVersion, configArgs, err := parseAppArguments(os.Args[1:])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}

	if printVersion {
		fmt.Println("TcpRoute2 version", strings.TrimSpace(version))
		return
	}

	serverConfig := new(ServerConfig)
	loader := multiconfig.MultiLoader(
		&multiconfig.TagLoader{},
		&multiconfig.TOMLLoader{Path: configPath},
		&multiconfig.EnvironmentLoader{},
		&multiconfig.FlagLoader{Args: configArgs},
	)
	if err := loader.Load(serverConfig); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if len(serverConfig.PreHttpPorts) == 0 && len(serverConfig.PreHttpsPorts) == 0 {
		serverConfig.PreHttpPorts = []int{80}
		serverConfig.PreHttpsPorts = []int{443}
	}
	preHttpPorts = serverConfig.PreHttpPorts
	preHttpsPorts = serverConfig.PreHttpsPorts
	configBase, err := configurationBaseDirectory(configPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if err := initializeWindowsNetwork(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if err := netchan.ConfigureDNS(serverConfig.Dns); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}

	// Create one fastest-route pool. Each upstream carries its own whitelist and
	// blacklist sources; routing remains an intrinsic part of upstream choice.
	// 创建一个最快线路池。每个上游携带自己的白名单和黑名单，分流仍属于线路选择本身。
	upStream := NewTcppingUpStream()
	if err := upStream.SetHostSources(serverConfig.Hosts, configBase); err != nil {
		panic(err)
	}
	// Direct is part of TcpRoute2's runtime contract. Keep it available even
	// when an advanced configuration only declares proxy upstreams (or none).
	// Clients may still serialize the explicit Direct entry for round-tripping.
	configs := make([]ServerConfigUpStream, 0, len(serverConfig.UpStreams))
	hasDirect := false
	for _, up := range serverConfig.UpStreams {
		configs = append(configs, up)
		if up.Remove {
			continue
		}
		proxyScheme := strings.SplitN(strings.TrimSpace(up.ProxyUrl), "://", 2)[0]
		if strings.EqualFold(proxyScheme, "direct") {
			hasDirect = true
		}
	}
	if !hasDirect {
		configs = append([]ServerConfigUpStream{{
			Name: "direct", ProxyUrl: "direct://0.0.0.0:0000", DnsResolve: true,
		}}, configs...)
	}
	configs, err = resolveUpstreamProxyReferences(configs)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	for _, up := range configs {
		if up.Remove {
			continue
		}
		if err := upStream.AddConfiguredUpStream(up, configBase); err != nil {
			panic(err)
		}
	}

	// 服务器监听
	if err := startWindowsTun(upStream, configs); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	srv := NewServer(serverConfig.Addr, upStream)
	if environmentEnabled("TCPROUTE_CLIENT_SESSION") {
		srv.OnListening = func() { fmt.Println("TCPROUTE_PROXY_READY") }
	}

	// DNS 配置

	// 各端口需要的安全级别

	if err := srv.ListAndServe(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
}

func configurationBaseDirectory(configPath string) (string, error) {
	base := os.Getenv("TCPROUTE_CONFIG_BASE")
	if base == "" {
		base = filepath.Dir(configPath)
	}
	base, err := filepath.Abs(base)
	if err != nil {
		return "", err
	}
	info, err := os.Stat(base)
	if err != nil || !info.IsDir() {
		return "", fmt.Errorf("configuration directory is unavailable: %s", base)
	}
	return base, nil
}

func parseAppArguments(args []string) (configPath string, printVersion bool, configArgs []string, err error) {
	configPath = "config.toml"
	configArgs = make([]string, 0, len(args))

	for index := 0; index < len(args); index++ {
		argument := args[index]
		switch {
		case argument == "-version" || argument == "--version":
			printVersion = true
		case argument == "-config" || argument == "--config":
			if index+1 >= len(args) {
				return "", false, nil, fmt.Errorf("%s requires a file path", argument)
			}
			index++
			configPath = args[index]
		case strings.HasPrefix(argument, "-config="):
			configPath = strings.TrimPrefix(argument, "-config=")
		case strings.HasPrefix(argument, "--config="):
			configPath = strings.TrimPrefix(argument, "--config=")
		default:
			configArgs = append(configArgs, argument)
		}
	}

	if configPath == "" {
		return "", false, nil, fmt.Errorf("config path must not be empty")
	}
	return configPath, printVersion, configArgs, nil
}
