package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/url"
	"os"
	"sort"
	"strings"
	"unicode/utf8"

	btoml "github.com/BurntSushi/toml"
	pt "github.com/pelletier/go-toml"
)

const subscriptionMarker = "# TcpRoute2 subscription "

type subscriptionImport struct {
	SourceID     string `json:"SourceId"`
	NodeID       string `json:"NodeId"`
	NodeName     string
	Upstream     ServerConfigUpStream
	LockedFields []string
}
type subscriptionIdentity struct {
	SourceID string `json:"SourceId"`
	NodeID   string `json:"NodeId"`
	NodeName string `json:",omitempty"`
	Delete   bool   `json:",omitempty"`
	Remove   bool   `json:",omitempty"`
}
type subscriptionTOMLRequest struct {
	Content             string
	Imports             []subscriptionImport
	Removals            []subscriptionIdentity
	RetainOwnershipFrom string
	IncludeRemoved      bool
	Migrate             bool
	ProtectMissing      bool
	Sources             []subscriptionSource
}
type subscriptionSource struct {
	SourceID       string `json:"SourceId"`
	Prefix         string
	Nodes          map[string]string
	NameIDPrefix   string `json:"NameIdPrefix"`
	PoolKnown      bool
	AvailableNodes []string
}
type subscriptionTOMLResult struct {
	Content    string
	Selections []subscriptionIdentity
}
type subscriptionMetadata struct {
	SourceID string `json:"SourceId"`
	NodeID   string `json:"NodeId"`
	NodeName string `json:",omitempty"`
}
type subscriptionTextEdit struct {
	start, end int
	value      string
}

func subscriptionScalarJSON(value any) ([]byte, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(value); err != nil {
		return nil, err
	}
	return bytes.TrimSuffix(buffer.Bytes(), []byte("\n")), nil
}

func editSubscriptionTOML(request subscriptionTOMLRequest) (subscriptionTOMLResult, error) {
	if request.RetainOwnershipFrom != "" {
		if len(request.Imports) != 0 || len(request.Removals) != 0 {
			return subscriptionTOMLResult{}, fmt.Errorf("cannot combine ownership retention with subscription imports")
		}
		request.RetainOwnershipFrom = ""
	}
	for _, imported := range request.Imports {
		suffix := imported.NodeID
		if !strings.HasSuffix(imported.Upstream.Name, "_"+suffix) {
			suffix = imported.NodeName
		}
		if suffix != "" && strings.HasSuffix(imported.Upstream.Name, "_"+suffix) {
			request.Sources = append(request.Sources, subscriptionSource{SourceID: imported.SourceID, Prefix: strings.TrimSuffix(imported.Upstream.Name, suffix), Nodes: map[string]string{imported.NodeID: imported.NodeName}})
		}
	}
	result := subscriptionTOMLResult{Content: request.Content, Selections: []subscriptionIdentity{}}
	tree, err := pt.Load(request.Content)
	if err != nil {
		return result, fmt.Errorf("invalid TOML; fix the configuration before importing subscriptions")
	}
	entries, _ := tree.Get("UpStreams").([]*pt.Tree)
	if tree.Get("UpStreams") != nil && entries == nil {
		return result, fmt.Errorf("subscription imports require [[UpStreams]] tables")
	}
	lines := strings.SplitAfter(request.Content, "\n")
	lineOffsets := make([]int, len(lines))
	for i := 1; i < len(lines); i++ {
		lineOffsets[i] = lineOffsets[i-1] + len(lines[i-1])
	}
	lineOffset := func(pos pt.Position) int {
		if pos.Line < 1 || pos.Line > len(lines) {
			return -1
		}
		text := lines[pos.Line-1]
		byteOffset := 0
		for col := 1; col < pos.Col && byteOffset < len(text); col++ {
			_, n := utf8.DecodeRuneInString(text[byteOffset:])
			byteOffset += n
		}
		return lineOffsets[pos.Line-1] + byteOffset
	}
	newline := "\n"
	if strings.Contains(request.Content, "\r\n") {
		newline = "\r\n"
	}
	owned := map[string]*pt.Tree{}
	metadata := map[*pt.Tree]subscriptionMetadata{}
	orphans := map[*pt.Tree]bool{}
	for _, entry := range entries {
		pos := entry.Position()
		if pos.Line < 1 || pos.Line > len(lines) {
			return result, fmt.Errorf("invalid upstream source position")
		}
		header := lines[pos.Line-1]
		if !strings.HasPrefix(strings.TrimSpace(header), "[[") {
			return result, fmt.Errorf("subscription imports require [[UpStreams]] tables")
		}
		marker := strings.Index(header, subscriptionMarker)
		var meta subscriptionMetadata
		if marker >= 0 {
			data, decodeErr := base64.RawURLEncoding.DecodeString(strings.TrimSpace(header[marker+len(subscriptionMarker):]))
			if decodeErr != nil || json.Unmarshal(data, &meta) != nil || meta.SourceID == "" || meta.NodeID == "" {
				return result, fmt.Errorf("invalid subscription ownership marker")
			}
		} else {
			name, _ := entry.Get("Name").(string)
			known := false
			bestRank, bestPrefix := -1, 0
			for _, source := range request.Sources {
				if source.SourceID == "" || !strings.HasPrefix(source.Prefix, "sub_") || !strings.HasSuffix(source.Prefix, "_") || !strings.HasPrefix(name, source.Prefix) || name == source.Prefix {
					continue
				}
				if meta.SourceID != "" && meta.SourceID != source.SourceID {
					return result, fmt.Errorf("ambiguous subscription upstream name %q", name)
				}
				suffix := strings.TrimPrefix(name, source.Prefix)
				id, nodeName := suffix, source.Nodes[suffix]
				_, matched := source.Nodes[suffix]
				cached := matched
				alias, _ := entry.Get("Alias").(string)
				if alias != "" {
					nodeName, matched = alias, true
				}
				for candidate, label := range source.Nodes {
					if alias != "" {
						break
					}
					if label != "" && suffix == label {
						id, nodeName, matched = candidate, label, true
						cached = true
						break
					}
				}
				if !matched {
					if meta.SourceID != "" {
						continue
					}
					// Legacy generated IDs were 24 hex digits; readable names recover
					// their normal name-derived ID even when the cache is missing.
					if _, err := hex.DecodeString(suffix); len(suffix) != 24 || err != nil {
						hash := sha256.Sum256([]byte(source.NameIDPrefix + suffix))
						id, nodeName = fmt.Sprintf("%x", hash[:12]), suffix
					}
				}
				rank := 0
				if matched {
					rank = 1
				}
				if cached {
					rank = 2
				}
				if rank < bestRank || (rank == bestRank && len(source.Prefix) < bestPrefix) {
					continue
				}
				if known && matched && rank == bestRank && len(source.Prefix) == bestPrefix && meta.NodeID != id {
					return result, fmt.Errorf("ambiguous subscription node name %q", name)
				}
				meta.SourceID, meta.NodeID, meta.NodeName = source.SourceID, id, nodeName
				known = known || matched
				bestRank, bestPrefix = rank, len(source.Prefix)
			}
			if meta.SourceID == "" {
				if request.ProtectMissing && strings.HasPrefix(name, "sub_") {
					orphans[entry] = true
				}
				continue
			}
		}
		key := meta.SourceID + "/" + meta.NodeID
		if owned[key] != nil {
			return result, fmt.Errorf("duplicate imported subscription node")
		}
		owned[key], metadata[entry] = entry, meta
	}
	if len(request.Imports) == 0 && len(request.Removals) == 0 && !request.Migrate && !request.ProtectMissing {
		for _, entry := range entries {
			if meta, ok := metadata[entry]; ok {
				if request.IncludeRemoved || entry.Get("Remove") != true {
					result.Selections = append(result.Selections, subscriptionIdentity{SourceID: meta.SourceID, NodeID: meta.NodeID, NodeName: meta.NodeName, Remove: entry.Get("Remove") == true})
				}
			}
		}
		return result, nil
	}
	var edits []subscriptionTextEdit
	removed := map[*pt.Tree]bool{}
	disabled := map[*pt.Tree]bool{}
	for _, identity := range request.Removals {
		if entry := owned[identity.SourceID+"/"+identity.NodeID]; entry != nil {
			if identity.Delete {
				removed[entry] = true
			} else {
				disabled[entry] = true
			}
		}
	}
	if request.ProtectMissing {
		configured, poolKnown := map[string]bool{}, map[string]bool{}
		available := map[string]map[string]bool{}
		for _, source := range request.Sources {
			configured[source.SourceID] = true
			if source.PoolKnown {
				poolKnown[source.SourceID] = true
				if available[source.SourceID] == nil {
					available[source.SourceID] = map[string]bool{}
				}
				for _, nodeID := range source.AvailableNodes {
					available[source.SourceID][nodeID] = true
				}
			}
		}
		for entry := range orphans {
			if !removed[entry] {
				disabled[entry] = true
			}
		}
		for entry, meta := range metadata {
			if !removed[entry] && (!configured[meta.SourceID] || (poolKnown[meta.SourceID] && !available[meta.SourceID][meta.NodeID])) {
				disabled[entry] = true
			}
		}
	}
	renamed := map[string]string{}
	for entry, meta := range metadata {
		name, _ := entry.Get("Name").(string)
		if meta.NodeName != "" && strings.HasSuffix(name, "_"+meta.NodeName) {
			canonical := strings.TrimSuffix(name, meta.NodeName) + meta.NodeID
			if canonical != name {
				renamed[name] = canonical
			}
		}
	}
	for _, imported := range request.Imports {
		if entry := owned[imported.SourceID+"/"+imported.NodeID]; entry != nil {
			if oldName, ok := entry.Get("Name").(string); ok && oldName != "" && oldName != imported.Upstream.Name {
				renamed[oldName] = imported.Upstream.Name
			}
		}
	}
	renamedPrefixes := map[string]string{}
	for entry, meta := range metadata {
		oldName, _ := entry.Get("Name").(string)
		newName := renamed[oldName]
		if newName == "" || !strings.HasSuffix(newName, "_"+meta.NodeID) {
			continue
		}
		oldPrefix := ""
		if strings.HasSuffix(oldName, "_"+meta.NodeID) {
			oldPrefix = strings.TrimSuffix(oldName, meta.NodeID)
		} else if meta.NodeName != "" && strings.HasSuffix(oldName, "_"+meta.NodeName) {
			oldPrefix = strings.TrimSuffix(oldName, meta.NodeName)
		}
		newPrefix := strings.TrimSuffix(newName, meta.NodeID)
		if oldPrefix == "" || oldPrefix == newPrefix {
			continue
		}
		if previous, exists := renamedPrefixes[oldPrefix]; exists && previous != newPrefix {
			return result, fmt.Errorf("ambiguous subscription prefix rename")
		}
		renamedPrefixes[oldPrefix] = newPrefix
	}
	fieldValues := func(config ServerConfigUpStream) map[string]any {
		return map[string]any{"Name": config.Name, "Alias": config.Alias, "ProxyUrl": config.ProxyUrl, "DialProxyFor": config.DialProxyFor, "Remove": config.Remove, "DnsResolve": config.DnsResolve, "Credit": int64(config.Credit), "Sleep": int64(config.Sleep), "CorrectDelay": int64(config.CorrectDelay)}
	}
	setScalar := func(entry *pt.Tree, key string, value any) error {
		encoded, encodeErr := subscriptionScalarJSON(value)
		if encodeErr != nil {
			return encodeErr
		}
		if !entry.Has(key) {
			pos := entry.Position()
			at := lineOffsets[pos.Line-1] + len(lines[pos.Line-1])
			separator := ""
			if !strings.HasSuffix(lines[pos.Line-1], "\n") {
				separator = newline
			}
			edits = append(edits, subscriptionTextEdit{at, at, separator + key + "=" + string(encoded) + newline})
			return nil
		}
		start := lineOffset(entry.GetPosition(key))
		if start < 0 {
			return fmt.Errorf("invalid upstream field position")
		}
		from, to, spanErr := subscriptionScalarSpan(request.Content, start)
		if spanErr != nil {
			return spanErr
		}
		edits = append(edits, subscriptionTextEdit{from, to, string(encoded)})
		return nil
	}
	appendix := ""
	importedEntries := map[*pt.Tree]bool{}
	for _, imported := range request.Imports {
		if imported.SourceID == "" || imported.NodeID == "" || !strings.HasPrefix(imported.Upstream.Name, "sub_") {
			return result, fmt.Errorf("invalid subscription import identity or managed name")
		}
		updatedURL, rewriteErr := renameSubscriptionReferences(imported.Upstream.ProxyUrl, renamed, 0)
		if rewriteErr != nil {
			return result, rewriteErr
		}
		imported.Upstream.ProxyUrl = updatedURL
		imported.Upstream.DialProxyFor = renameDialProxyTargets(imported.Upstream.DialProxyFor, renamed, renamedPrefixes)
		values := fieldValues(imported.Upstream)
		entry := owned[imported.SourceID+"/"+imported.NodeID]
		header := "[[UpStreams]]" + newline
		if entry == nil {
			for _, other := range entries {
				if other.Get("Name") == imported.Upstream.Name {
					return result, fmt.Errorf("upstream name %q already belongs to an existing entry", imported.Upstream.Name)
				}
			}
			appendix += newline + header
			for _, key := range []string{"Name", "Alias", "ProxyUrl", "DialProxyFor", "Remove", "DnsResolve", "Credit", "Sleep", "CorrectDelay"} {
				if key == "Alias" && values[key] == "" {
					continue
				}
				if key == "DialProxyFor" && values[key] == "" {
					continue
				}
				encoded, _ := subscriptionScalarJSON(values[key])
				appendix += key + "=" + string(encoded) + newline
			}
			continue
		}
		if removed[entry] || disabled[entry] {
			return result, fmt.Errorf("cannot import and remove the same subscription node")
		}
		importedEntries[entry] = true
		oldName, _ := entry.Get("Name").(string)
		if oldName != "" && oldName != imported.Upstream.Name {
			renamed[oldName] = imported.Upstream.Name
		}
		for _, key := range []string{"Name", "Alias", "ProxyUrl", "DialProxyFor", "Remove", "DnsResolve", "Credit", "Sleep", "CorrectDelay"} {
			if key == "Alias" && values[key] == "" && !entry.Has(key) {
				continue
			}
			if key == "DialProxyFor" && values[key] == "" && !entry.Has(key) {
				continue
			}
			locked := false
			for _, field := range imported.LockedFields {
				if field == key {
					locked = true
				}
			}
			if locked && key != "Name" && key != "Remove" && key != "ProxyUrl" && entry.Has(key) {
				continue
			}
			value := values[key]
			if key == "ProxyUrl" {
				current, _ := entry.Get(key).(string)
				updated, mergeErr := applySubscriptionURLLocks(current, imported.Upstream.ProxyUrl, imported.LockedFields)
				if mergeErr != nil {
					return result, mergeErr
				}
				if setErr := setScalar(entry, key, updated); setErr != nil {
					return result, setErr
				}
			} else {
				if setErr := setScalar(entry, key, value); setErr != nil {
					return result, setErr
				}
			}
		}
	}
	for entry, meta := range metadata {
		if removed[entry] {
			continue
		}
		if !importedEntries[entry] {
			if meta.NodeName != "" {
				if err := setScalar(entry, "Alias", meta.NodeName); err != nil {
					return result, err
				}
			}
			if name, _ := entry.Get("Name").(string); renamed[name] != "" {
				if err := setScalar(entry, "Name", renamed[name]); err != nil {
					return result, err
				}
			}
		}
		pos := entry.Position()
		if marker := strings.Index(lines[pos.Line-1], subscriptionMarker); marker >= 0 {
			header := strings.TrimRight(lines[pos.Line-1][:marker], " \t") + newline
			edits = append(edits, subscriptionTextEdit{lineOffsets[pos.Line-1], lineOffsets[pos.Line-1] + len(lines[pos.Line-1]), header})
		}
	}
	for entry := range disabled {
		if err := setScalar(entry, "Remove", true); err != nil {
			return result, err
		}
	}
	// Tree positions identify real tables, so multiline strings containing fake
	// table headers cannot affect ownership, deletion boundaries, or edits.
	var tables []*pt.Tree
	var collect func(*pt.Tree)
	collect = func(node *pt.Tree) {
		if removed[node] {
			return
		}
		tables = append(tables, node)
		for _, key := range node.Keys() {
			switch child := node.GetPath([]string{key}).(type) {
			case *pt.Tree:
				collect(child)
			case []*pt.Tree:
				for _, item := range child {
					collect(item)
				}
			}
		}
	}
	collect(tree)
	for entry := range removed {
		start := lineOffsets[entry.Position().Line-1]
		end := len(request.Content)
		for _, other := range tables {
			pos := other.Position()
			if pos.Line > entry.Position().Line && pos.Line <= len(lines) {
				at := lineOffsets[pos.Line-1]
				if at < end {
					end = at
				}
			}
		}
		// Consecutive removed entries form one deletion, avoiding overlapping edits.
		for other := range removed {
			if other != entry && other.Position().Line > entry.Position().Line {
				at := lineOffsets[other.Position().Line-1]
				if at < end {
					end = at
				}
			}
		}
		edits = append(edits, subscriptionTextEdit{start, end, ""})
	}
	for _, entry := range entries {
		if removed[entry] {
			continue
		}
		if current, ok := entry.Get("DialProxyFor").(string); ok {
			if replacement := renameDialProxyTargets(current, renamed, renamedPrefixes); replacement != current {
				from, to, spanErr := subscriptionScalarSpan(request.Content, lineOffset(entry.GetPosition("DialProxyFor")))
				if spanErr != nil {
					return result, spanErr
				}
				found := false
				for index := range edits {
					if edits[index].start == from && edits[index].end == to {
						var value string
						if json.Unmarshal([]byte(edits[index].value), &value) == nil {
							if renamedValue := renameDialProxyTargets(value, renamed, renamedPrefixes); renamedValue != value {
								encoded, _ := subscriptionScalarJSON(renamedValue)
								edits[index].value = string(encoded)
							}
							found = true
						}
						break
					}
				}
				if !found {
					if err := setScalar(entry, "DialProxyFor", replacement); err != nil {
						return result, err
					}
				}
			}
		}
		current, _ := entry.Get("ProxyUrl").(string)
		updated, rewriteErr := renameSubscriptionReferences(current, renamed, 0)
		if rewriteErr != nil {
			return result, rewriteErr
		}
		if updated != current {
			// Imports may already have replaced this field; rewrite its new value.
			start := lineOffset(entry.GetPosition("ProxyUrl"))
			from, to, spanErr := subscriptionScalarSpan(request.Content, start)
			if spanErr != nil {
				return result, spanErr
			}
			found := false
			for index := range edits {
				if edits[index].start == from && edits[index].end == to {
					var value string
					if json.Unmarshal([]byte(edits[index].value), &value) == nil {
						updated, rewriteErr = renameSubscriptionReferences(value, renamed, 0)
						if rewriteErr != nil {
							return result, rewriteErr
						}
						encoded, _ := subscriptionScalarJSON(updated)
						edits[index].value = string(encoded)
						found = true
					}
					break
				}
			}
			if !found {
				if setErr := setScalar(entry, "ProxyUrl", updated); setErr != nil {
					return result, setErr
				}
			}
		}
	}
	sort.SliceStable(edits, func(i, j int) bool { return edits[i].start > edits[j].start })
	next := request.Content
	boundary := len(next) + 1
	for _, edit := range edits {
		if edit.end > boundary {
			return result, fmt.Errorf("overlapping subscription edits")
		}
		next = next[:edit.start] + edit.value + next[edit.end:]
		boundary = edit.start
	}
	if appendix != "" {
		if !strings.HasSuffix(next, "\n") {
			next += newline
		}
		next += appendix
	}
	var config ServerConfig
	if _, err := btoml.Decode(next, &config); err != nil {
		return result, fmt.Errorf("subscription edit produced invalid TOML")
	}
	if _, err := resolveUpstreamProxyReferences(config.UpStreams); err != nil {
		return result, fmt.Errorf("subscription edit rejected: %w", err)
	}
	updated, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: next, Sources: request.Sources, IncludeRemoved: request.IncludeRemoved})
	if err != nil {
		return result, err
	}
	result.Selections = updated.Selections
	result.Content = next
	counts := map[string]int{}
	for _, selection := range result.Selections {
		counts[selection.SourceID]++
		if counts[selection.SourceID] > 2048 {
			return result, fmt.Errorf("at most 2048 nodes per subscription may be imported into a profile")
		}
	}
	if len(result.Selections) > 4096 {
		return result, fmt.Errorf("at most 4096 subscription upstreams may be imported into a profile")
	}
	return result, nil
}

func subscriptionScalarSpan(content string, start int) (int, int, error) {
	if start < 0 || start >= len(content) {
		return 0, 0, fmt.Errorf("invalid upstream field location")
	}
	at := strings.IndexByte(content[start:], '=')
	if at < 0 {
		return 0, 0, fmt.Errorf("invalid upstream assignment")
	}
	at += start + 1
	for at < len(content) && (content[at] == ' ' || content[at] == '\t') {
		at++
	}
	from := at
	if at >= len(content) {
		return 0, 0, fmt.Errorf("missing upstream value")
	}
	if content[at] == '"' || content[at] == '\'' {
		quote := content[at]
		delimiter := string(quote)
		if strings.HasPrefix(content[at:], strings.Repeat(delimiter, 3)) {
			delimiter = strings.Repeat(delimiter, 3)
		}
		at += len(delimiter)
		for at < len(content) {
			if quote == '"' && content[at] == '\\' {
				at += 2
				continue
			}
			if strings.HasPrefix(content[at:], delimiter) {
				return from, at + len(delimiter), nil
			}
			at++
		}
		return 0, 0, fmt.Errorf("unterminated upstream string")
	}
	for at < len(content) && !strings.ContainsRune(" \t\r\n#", rune(content[at])) {
		at++
	}
	return from, at, nil
}

func renameSubscriptionReferences(raw string, names map[string]string, depth int) (string, error) {
	if len(names) == 0 {
		return raw, nil
	}
	if depth > 64 {
		return "", fmt.Errorf("proxy chain is too deep")
	}
	parsed, err := url.Parse(raw)
	if err != nil {
		return "", fmt.Errorf("invalid upstream URL")
	}
	query, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid upstream query")
	}
	changed := false
	for key, values := range query {
		if !strings.EqualFold(key, "upProxy") {
			continue
		}
		for i, value := range values {
			if replacement, ok := names[value]; ok {
				values[i] = replacement
				changed = true
			} else if strings.Contains(value, "://") {
				replacement, innerErr := renameSubscriptionReferences(value, names, depth+1)
				if innerErr != nil {
					return "", innerErr
				}
				if replacement != value {
					values[i] = replacement
					changed = true
				}
			}
		}
		query[key] = values
	}
	if !changed {
		return raw, nil
	}
	parsed.RawQuery = query.Encode()
	return parsed.String(), nil
}

func applySubscriptionURLLocks(current, desired string, fields []string) (string, error) {
	lockParent, lockCredit := false, false
	for _, field := range fields {
		lockParent = lockParent || field == "UpProxy"
		lockCredit = lockCredit || field == "Credit"
	}
	if !lockParent && !lockCredit {
		return desired, nil
	}
	parsed, err := url.Parse(current)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription URL")
	}
	target, err := url.Parse(desired)
	if err != nil {
		return "", fmt.Errorf("invalid generated subscription URL")
	}
	query, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription query")
	}
	targetQuery, err := url.ParseQuery(target.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid generated subscription query")
	}
	for key := range targetQuery {
		if (lockParent && strings.EqualFold(key, "upProxy")) || (lockCredit && strings.EqualFold(key, "credit")) {
			delete(targetQuery, key)
		}
	}
	for key, value := range query {
		if (lockParent && strings.EqualFold(key, "upProxy")) || (lockCredit && strings.EqualFold(key, "credit")) {
			targetQuery[key] = value
		}
	}
	if targetQuery.Encode() == target.Query().Encode() {
		return desired, nil
	}
	target.RawQuery = targetQuery.Encode()
	return target.String(), nil
}

func runSubscriptionTOMLEditor() int {
	var request subscriptionTOMLRequest
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 16*1024*1024))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		fmt.Fprintln(os.Stderr, "invalid subscription edit request")
		return 2
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		fmt.Fprintln(os.Stderr, "unexpected subscription edit request data")
		return 2
	}
	result, err := editSubscriptionTOML(request)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	if err := json.NewEncoder(os.Stdout).Encode(result); err != nil {
		return 2
	}
	return 0
}
