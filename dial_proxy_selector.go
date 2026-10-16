package main

import (
	"fmt"
	"sort"
	"strings"
)

func validateDialProxySelectors(value, owner string, byName map[string]int) error {
	value = strings.TrimSpace(value)
	for _, selector := range strings.Split(value, ",") {
		if strings.TrimSpace(selector) == "*" {
			return fmt.Errorf("DialProxyFor does not allow the standalone '*' selector")
		}
	}
	if owner == "" {
		return nil
	}
	if _, exact := byName[value]; exact {
		if value == owner {
			return fmt.Errorf("DialProxyFor cannot select itself")
		}
		return nil
	}
	for _, selector := range strings.Split(value, ",") {
		selector = strings.TrimSpace(selector)
		if selector == "" {
			continue
		}
		if _, exact := byName[selector]; exact {
			if selector == owner {
				return fmt.Errorf("DialProxyFor cannot select itself")
			}
		} else if strings.HasPrefix(owner, strings.TrimSuffix(selector, "*")) {
			return fmt.Errorf("DialProxyFor cannot select itself")
		}
	}
	return nil
}

func dialProxyTargets(value string, configs []ServerConfigUpStream, byName map[string]int) ([]int, error) {
	value = strings.TrimSpace(value)
	if err := validateDialProxySelectors(value, "", byName); err != nil {
		return nil, err
	}
	// Preserve existing names, including names containing commas.
	if target, ok := byName[value]; ok {
		return []int{target}, nil
	}
	var targets []int
	seen := make(map[int]bool)
	for _, selector := range strings.Split(value, ",") {
		selector = strings.TrimSpace(selector)
		if selector == "" {
			return nil, fmt.Errorf("DialProxyFor contains an empty selector")
		}
		var matches []int
		if target, ok := byName[selector]; ok {
			matches = []int{target}
		} else {
			prefix := strings.TrimSuffix(selector, "*")
			for index, config := range configs {
				if strings.HasPrefix(config.Name, prefix) {
					matches = append(matches, index)
				}
			}
		}
		if len(matches) == 0 {
			return nil, fmt.Errorf("DialProxyFor selector %q does not match an upstream Name", selector)
		}
		for _, target := range matches {
			if !seen[target] {
				targets = append(targets, target)
				seen[target] = true
			}
		}
	}
	return targets, nil
}

func renameDialProxyTargets(value string, names, prefixes map[string]string) string {
	if replacement, ok := names[strings.TrimSpace(value)]; ok {
		return replacement
	}
	keys := make([]string, 0, len(prefixes))
	for prefix := range prefixes {
		keys = append(keys, prefix)
	}
	sort.Slice(keys, func(i, j int) bool { return len(keys[i]) > len(keys[j]) })
	selectors := strings.Split(value, ",")
	changed := false
	for index, raw := range selectors {
		selector := strings.TrimSpace(raw)
		if replacement, ok := names[selector]; ok {
			selectors[index], changed = replacement, true
			continue
		}
		for _, prefix := range keys {
			if strings.HasPrefix(selector, prefix) {
				selectors[index] = prefixes[prefix] + strings.TrimPrefix(selector, prefix)
				changed = true
				break
			}
		}
	}
	if !changed {
		return value
	}
	return strings.Join(selectors, ",")
}
