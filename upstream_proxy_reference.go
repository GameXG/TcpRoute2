package main

import (
	"fmt"
	"net/url"
	"strings"
)

func proxyURLParts(raw, owner string) (*url.URL, url.Values, string, error) {
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme == "" {
		return nil, nil, "", fmt.Errorf("upstream %q has invalid ProxyUrl", owner)
	}
	query, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return nil, nil, "", fmt.Errorf("upstream %q has invalid proxy URL query", owner)
	}
	key := ""
	for candidate, values := range query {
		if !strings.EqualFold(candidate, "upProxy") {
			continue
		}
		if key != "" || len(values) != 1 || strings.TrimSpace(values[0]) == "" {
			return nil, nil, "", fmt.Errorf("upstream %q has invalid or duplicate upProxy parameters", owner)
		}
		key = candidate
	}
	return parsed, query, key, nil
}

func proxyAcceptsParent(scheme string) bool {
	switch strings.ToLower(scheme) {
	case "http", "https", "socks5", "socks4", "socks4a", "ss", "shadowsocks", "vmess", "vless", "trojan", "wireguard", "hysteria", "hysteria2", "hy2":
		return true
	}
	return false
}

// Resolve both forms into ProxyClient URLs without changing the saved TOML.
// DialProxyFor adds a parent relationship without removing a route's own use.
func resolveUpstreamProxyReferences(configs []ServerConfigUpStream) ([]ServerConfigUpStream, error) {
	configs = append([]ServerConfigUpStream(nil), configs...)
	byName := make(map[string]int, len(configs))
	for index := range configs {
		name := strings.TrimSpace(configs[index].Name)
		if _, exists := byName[name]; exists {
			return nil, fmt.Errorf("duplicate upstream name: %s", name)
		}
		configs[index].Name = name
		configs[index].DialProxyFor = strings.TrimSpace(configs[index].DialProxyFor)
		byName[name] = index
	}
	parents := make([][]int, len(configs))
	hasDialProxyFor := false
	for index, config := range configs {
		if config.DialProxyFor == "" {
			continue
		}
		if err := validateDialProxySelectors(config.DialProxyFor, config.Name, byName); err != nil {
			return nil, fmt.Errorf("upstream %q: %w", config.Name, err)
		}
		if config.Remove {
			continue
		}
		hasDialProxyFor = true
		_, _, key, err := proxyURLParts(config.ProxyUrl, config.Name)
		if err != nil {
			return nil, err
		}
		if key != "" {
			return nil, fmt.Errorf("upstream %q: DialProxyFor and upProxy are mutually exclusive", config.Name)
		}
		targets, err := dialProxyTargets(config.DialProxyFor, configs, byName)
		if err != nil {
			return nil, fmt.Errorf("upstream %q: %w", config.Name, err)
		}
		for _, target := range targets {
			if target == index {
				return nil, fmt.Errorf("upstream %q: DialProxyFor cannot select itself", config.Name)
			}
			if configs[target].Remove {
				continue
			}
			parsed, _, targetKey, err := proxyURLParts(configs[target].ProxyUrl, configs[target].Name)
			if err != nil {
				return nil, err
			}
			if targetKey != "" {
				return nil, fmt.Errorf("upstream %q: upProxy conflicts with incoming DialProxyFor", configs[target].Name)
			}
			if !proxyAcceptsParent(parsed.Scheme) {
				return nil, fmt.Errorf("upstream %q: DialProxyFor chaining is not supported on %s", configs[target].Name, parsed.Scheme)
			}
			parents[target] = append(parents[target], index)
		}
	}
	state := make([]uint8, len(configs))
	cache := make([][]string, len(configs))
	active := 0
	var resolve func(int) ([]string, error)
	var expand func(string, string, int) ([]string, error)
	expand = func(raw, owner string, depth int) ([]string, error) {
		if depth > 64 {
			return nil, fmt.Errorf("upstream %q has too many nested upProxy URLs", owner)
		}
		parsed, query, key, err := proxyURLParts(raw, owner)
		if err != nil {
			return nil, err
		}
		if key == "" {
			return []string{raw}, nil
		}
		value := query[key][0]
		var variants []string
		if strings.Contains(value, "://") {
			variants, err = expand(value, owner, depth+1)
		} else {
			target, ok := byName[value]
			if !ok {
				return nil, fmt.Errorf("upstream %q references unknown upProxy upstream %q", owner, value)
			}
			variants, err = resolve(target)
		}
		if err != nil {
			return nil, err
		}
		urls := make([]string, 0, len(variants))
		for _, variant := range variants {
			if variant == value {
				urls = append(urls, raw)
				continue
			}
			if !proxyAcceptsParent(parsed.Scheme) {
				return nil, fmt.Errorf("upstream %q: named upProxy is not supported on %s by the pinned ProxyClient", owner, parsed.Scheme)
			}
			query.Set(key, variant)
			parsed.RawQuery = query.Encode()
			urls = append(urls, parsed.String())
		}
		return urls, nil
	}
	resolve = func(index int) ([]string, error) {
		if configs[index].Remove {
			return nil, nil
		}
		if state[index] == 1 {
			return nil, fmt.Errorf("proxy chain reference cycle at upstream %q", configs[index].Name)
		}
		if state[index] == 2 {
			return cache[index], nil
		}
		if active >= 64 {
			return nil, fmt.Errorf("upstream %q exceeds 64 proxy hops", configs[index].Name)
		}
		state[index] = 1
		active++
		defer func() { active-- }()
		var urls []string
		if len(parents[index]) == 0 {
			var err error
			urls, err = expand(configs[index].ProxyUrl, configs[index].Name, 0)
			if err != nil {
				return nil, err
			}
		} else {
			parsed, query, _, err := proxyURLParts(configs[index].ProxyUrl, configs[index].Name)
			if err != nil {
				return nil, err
			}
			for _, parent := range parents[index] {
				variants, err := resolve(parent)
				if err != nil {
					return nil, err
				}
				for _, variant := range variants {
					query.Set("upProxy", variant)
					parsed.RawQuery = query.Encode()
					urls = append(urls, parsed.String())
					if len(urls) > 256 {
						return nil, fmt.Errorf("upstream %q exceeds 256 proxy-chain candidates", configs[index].Name)
					}
				}
			}
		}
		cache[index] = urls
		state[index] = 2
		return urls, nil
	}
	for index := range configs {
		if _, err := resolve(index); err != nil {
			return nil, err
		}
	}
	result := make([]ServerConfigUpStream, 0, len(configs))
	used := make(map[string]bool, len(configs))
	for _, config := range configs {
		used[config.Name] = true
	}
	for index, config := range configs {
		for variant, proxyURL := range cache[index] {
			runtime := config
			runtime.ProxyUrl = proxyURL
			if len(cache[index]) > 1 {
				name := fmt.Sprintf("%s [chain %d]", config.Name, variant+1)
				for used[name] {
					name += "_"
				}
				runtime.Name = name
				used[name] = true
			}
			result = append(result, runtime)
			if hasDialProxyFor && len(result) > 4096 {
				return nil, fmt.Errorf("DialProxyFor produces too many runtime routes")
			}
		}
	}
	return result, nil
}
