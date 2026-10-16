package main

import (
	"encoding/base64"
	"encoding/json"
	"net/url"
	"strings"
	"testing"

	btoml "github.com/BurntSushi/toml"
)

func TestSubscriptionReadableNamesMigrateLegacyMetadata(t *testing.T) {
	name := "\u9999\u6e2f \u00b7 01"
	legacy := map[string]any{"SourceId": "feed", "NodeId": "9824086d034c33939dc44b77", "NodeName": name, "Baseline": map[string]string{"Sleep": "obsolete"}, "QueryBaseline": map[string]string{"extra": "obsolete"}}
	data, _ := json.Marshal(legacy)
	oldName := "sub_old_9824086d034c33939dc44b77"
	content := "addr='127.0.0.1:7070'\n[[UpStreams]] " + subscriptionMarker + base64.RawURLEncoding.EncodeToString(data) + "\nName='" + oldName + "'\nProxyUrl='https://old.test:443?upProxy=parent&custom=yes'\nSleep=19 # retained comment\nCredit=-5\nDnsResolve=true\n[[UpStreams.Whitelist]]\nPath='rules.txt'\nType='suffix'\n[[UpStreams]]\nName='parent'\nProxyUrl='socks5://parent.test:1080'\n[[UpStreams]]\nName='chain'\nProxyUrl='http://chain.test:8080?upProxy=" + oldName + "'\n"
	imported := subscriptionImport{SourceID: "feed", NodeID: "9824086d034c33939dc44b77", NodeName: name, LockedFields: []string{"Sleep", "UpProxy"}, Upstream: ServerConfigUpStream{Name: "sub_new_9824086d034c33939dc44b77", Alias: name, ProxyUrl: "https://new.test:443?security=tls&extra=new", Sleep: 80, Credit: 10, DnsResolve: false}}
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: content, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(result.Content, subscriptionMarker) || strings.Contains(result.Content, "\\u0026") || !strings.Contains(result.Content, name) || !strings.Contains(result.Content, "Sleep=19 # retained comment") || !strings.Contains(result.Content, "Credit=10") || !strings.Contains(result.Content, "DnsResolve=false") || !strings.Contains(result.Content, "Path='rules.txt'") {
		t.Fatalf("migration did not preserve only locked fields and rules in readable TOML:\n%s", result.Content)
	}
	var config ServerConfig
	if _, err := btoml.Decode(result.Content, &config); err != nil {
		t.Fatal(err)
	}
	if config.UpStreams[0].Name != imported.Upstream.Name || config.UpStreams[0].Alias != name {
		t.Fatal("stable name or alias was lost")
	}
	proxy, _ := url.Parse(config.UpStreams[0].ProxyUrl)
	chain, _ := url.Parse(config.UpStreams[2].ProxyUrl)
	if proxy.Host != "new.test:443" || proxy.Query().Get("upProxy") != "parent" || proxy.Query().Get("custom") != "" || chain.Query().Get("upProxy") != imported.Upstream.Name {
		t.Fatalf("migration broke URL locks or chain references: %+v", config.UpStreams)
	}
	sources := []subscriptionSource{{SourceID: "feed", Prefix: "sub_new_"}}
	query, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Sources: sources})
	if err != nil || len(query.Selections) != 1 || query.Selections[0].NodeID != imported.NodeID || query.Selections[0].NodeName != name {
		t.Fatalf("readable name lost identity after reload: %+v %v", query, err)
	}
	removed, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Sources: sources, Removals: []subscriptionIdentity{{SourceID: "feed", NodeID: imported.NodeID}}})
	if err != nil || len(removed.Selections) != 0 || !strings.Contains(removed.Content, "Remove=true") {
		t.Fatalf("readable node could not be deselected: %v", err)
	}
	restored, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: removed.Content, Sources: sources, Imports: []subscriptionImport{imported}})
	if err != nil || len(restored.Selections) != 1 || strings.Contains(restored.Content, subscriptionMarker) {
		t.Fatalf("readable node could not be reselected: %v", err)
	}
}

func TestSubscriptionReadableNameMigratesToStableNameAndAlias(t *testing.T) {
	oldName := "sub_mysub_Alpha"
	content := "addr='127.0.0.1:7070'\n[[UpStreams]]\nName='" + oldName + "'\nProxyUrl='socks5://old.test:1080'\n[[UpStreams]]\nName='chain'\nProxyUrl='http://chain.test:8080?upProxy=" + oldName + "'\n"
	imported := sampleSubscriptionImport()
	imported.NodeName, imported.Upstream.Alias = "Alpha", "Alpha"
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: content, Sources: []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_", Nodes: map[string]string{"a": "Alpha"}}}, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, `Name="sub_mysub_a"`) || !strings.Contains(result.Content, `Alias="Alpha"`) || !strings.Contains(result.Content, "upProxy=sub_mysub_a") || len(result.Selections) != 1 || result.Selections[0].NodeID != "a" || result.Selections[0].NodeName != "Alpha" {
		t.Fatalf("readable-name migration lost identity, alias, or references:\n%s", result.Content)
	}
}

func TestSubscriptionAliasWithOverlappingSourcePrefixes(t *testing.T) {
	for _, nodeID := range []string{"a", "bar_a"} {
		name := "sub_foo_" + nodeID
		if nodeID == "a" {
			name = "sub_foo_bar_a"
		}
		result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "[[UpStreams]]\nName='" + name + "'\nAlias='Alpha'\nProxyUrl='socks5://proxy.test:1080'\n", Sources: []subscriptionSource{
			{SourceID: "feed", Prefix: "sub_foo_", Nodes: map[string]string{nodeID: "Alpha"}},
			{SourceID: "feed", Prefix: "sub_foo_bar_", Nodes: map[string]string{nodeID: "Alpha"}},
		}})
		if err != nil || len(result.Selections) != 1 || result.Selections[0].NodeID != nodeID {
			t.Fatalf("prefix history changed stable ID %q: %+v %v", nodeID, result.Selections, err)
		}
	}
}

func TestSubscriptionMigrationRetainsDisabledNodes(t *testing.T) {
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "[[UpStreams]]\nName='sub_mysub_Alpha'\nProxyUrl='socks5://proxy.test:1080'\nRemove=true\n", Sources: []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_", Nodes: map[string]string{"a": "Alpha"}}}, Migrate: true})
	if err != nil || len(result.Selections) != 0 || !strings.Contains(result.Content, `Name="sub_mysub_a"`) || !strings.Contains(result.Content, `Alias="Alpha"`) || !strings.Contains(result.Content, "Remove=true") {
		t.Fatalf("migration lost a disabled node or re-enabled it: %v\n%s", err, result.Content)
	}
}

func TestSubscriptionRenameRepairsDialProxySelectors(t *testing.T) {
	for _, selectors := range []string{"sub_mysub_*,other", "sub_mysub_a,other", "sub_mysub_,other"} {
		base := "[[UpStreams]]\nName='accelerator'\nProxyUrl='socks5://accelerator.test:1080'\nDialProxyFor='" + selectors + "'\n[[UpStreams]]\nName='other'\nProxyUrl='https://other.test:443'\n"
		imported := sampleSubscriptionImport()
		initial, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: base, Imports: []subscriptionImport{imported}})
		if err != nil {
			t.Fatal(err)
		}
		imported.Upstream.Name = "sub_office_a"
		result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: initial.Content, Imports: []subscriptionImport{imported}})
		if err != nil {
			t.Fatal(err)
		}
		want := strings.ReplaceAll(selectors, "sub_mysub_", "sub_office_")
		if !strings.Contains(result.Content, `DialProxyFor="`+want+`"`) {
			t.Fatalf("rename lost selector %q:\n%s", selectors, result.Content)
		}
	}
}

func sampleSubscriptionImport() subscriptionImport {
	return subscriptionImport{SourceID: "mysub", NodeID: "a", Upstream: ServerConfigUpStream{Name: "sub_mysub_a", ProxyUrl: "socks5://127.0.0.1:1080", Sleep: 80}}
}

func TestSubscriptionKnownDeselectionDeletesOnlyOwnedTable(t *testing.T) {
	content := "addr='127.0.0.1:7070'\n[[UpStreams]]\nName='sub_mysub_a'\nProxyUrl='socks5://127.0.0.1:1080'\n[[UpStreams.Whitelist]]\nType='suffix'\nPath='owned.txt'\n[[UpStreams]] # manual\nName='direct'\nProxyUrl='direct://0.0.0.0:0'\n[[UpStreams.Blacklist]]\nType='suffix'\nPath='manual.txt'\n"
	result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: content, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a", Delete: true}}})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(result.Content, "sub_mysub_a") || strings.Contains(result.Content, "owned.txt") || strings.Contains(result.Content, "Remove=") || !strings.Contains(result.Content, "Path='manual.txt'") || !strings.Contains(result.Content, "[[UpStreams]] # manual") {
		t.Fatalf("deselection left a placeholder or changed manual configuration:\n%s", result.Content)
	}
	_, err = editTestSubscriptionTOML(subscriptionTOMLRequest{Content: content + "[[UpStreams]]\nName='chain'\nProxyUrl='http://127.0.0.1:8080?upProxy=sub_mysub_a'\n", Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a", Delete: true}}})
	if err == nil {
		t.Fatal("deletion accepted a dangling explicit chain reference")
	}
}

func TestSubscriptionStartupProtectsMissingWithoutDeleting(t *testing.T) {
	content := "addr='127.0.0.1:7070'\n[[UpStreams]]\nName='sub_mysub_a'\nProxyUrl='socks5://127.0.0.1:1080'\n[[UpStreams.Whitelist]]\nType='suffix'\nPath='keep.txt'\n"
	for _, tc := range []struct {
		name     string
		sources  []subscriptionSource
		disabled bool
	}{
		{"available", []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_", PoolKnown: true, AvailableNodes: []string{"a"}}}, false},
		{"missing node", []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_", PoolKnown: true}}, true},
		{"unavailable cache", []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_"}}, false},
		{"missing source", nil, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			for i := range tc.sources {
				tc.sources[i].Nodes = map[string]string{"a": "a"}
			}
			result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: content, Sources: tc.sources, ProtectMissing: true})
			if err != nil {
				t.Fatal(err)
			}
			var config ServerConfig
			if _, err := btoml.Decode(result.Content, &config); err != nil {
				t.Fatal(err)
			}
			if len(config.UpStreams) != 1 || config.UpStreams[0].Remove != tc.disabled || !strings.Contains(result.Content, "Path='keep.txt'") {
				t.Fatalf("protection failed: %s", result.Content)
			}
		})
	}
}

func editTestSubscriptionTOML(request subscriptionTOMLRequest) (subscriptionTOMLResult, error) {
	request.Sources = append(request.Sources,
		subscriptionSource{SourceID: "mysub", Prefix: "sub_mysub_", Nodes: map[string]string{"a": "a"}},
		subscriptionSource{SourceID: "mysub", Prefix: "sub_office_", Nodes: map[string]string{"a": "a"}})
	return editSubscriptionTOML(request)
}

func TestSubscriptionTOMLImportPreservesUserFile(t *testing.T) {
	original := "# preserve this\r\naddr='127.0.0.1:7070'\r\nnotes='''\r\n[[UpStreams]]\r\nName=\"fake\"\r\n'''\r\n[[UpStreams]] # manual\r\nName='direct'\r\nProxyUrl='direct://0.0.0.0:0'\r\nDnsResolve=true\r\n"
	imported := sampleSubscriptionImport()
	result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: original, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(result.Content, original) || !strings.Contains(result.Content, `Name="sub_mysub_a"`) {
		t.Fatal("original content changed or subscription was not written")
	}
	if len(result.Selections) != 1 || result.Selections[0].SourceID != "mysub" {
		t.Fatal("TOML ownership was not recorded")
	}
	again, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if again.Content != result.Content {
		t.Fatalf("reselect modified an unchanged import:\n%s", again.Content)
	}
}

func TestSubscriptionTOMLRefreshUsesExplicitLocks(t *testing.T) {
	imported := sampleSubscriptionImport()
	result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	custom := strings.Replace(result.Content, "Sleep=80", "Sleep=15 # user override", 1) + "\n[[UpStreams.Whitelist]]\nPath='custom.txt'\nType='suffix'\n"
	imported.Upstream.ProxyUrl = "socks5://127.0.0.1:1081"
	result, err = editTestSubscriptionTOML(subscriptionTOMLRequest{Content: custom, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, "Sleep=80 # user override") || !strings.Contains(result.Content, "Path='custom.txt'") || !strings.Contains(result.Content, "127.0.0.1:1081") {
		t.Fatalf("refresh failed to update unlocked fields or preserve rules and comments:\n%s", result.Content)
	}
	imported.LockedFields = []string{"Sleep"}
	custom = strings.Replace(result.Content, "Sleep=80", "Sleep=15", 1)
	result, err = editTestSubscriptionTOML(subscriptionTOMLRequest{Content: custom, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, "Sleep=15 # user override") {
		t.Fatal("locked field incorrectly overwrote an existing user value")
	}
}

func TestSubscriptionTOMLChainRenameAndReferencedRemoval(t *testing.T) {
	imported := sampleSubscriptionImport()
	result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	withChain := result.Content + "\n[[UpStreams]]\nName='chain'\nProxyUrl='http://127.0.0.1:8080?upProxy=sub_mysub_a'\n"
	softRemoved, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: withChain, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a"}}})
	if err != nil || !strings.Contains(softRemoved.Content, "Remove=true") || !strings.Contains(softRemoved.Content, "upProxy=sub_mysub_a") {
		t.Fatal("referenced upstream was not soft-removed")
	}
	imported.Upstream.Name = "sub_office_a"
	renamed, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: withChain, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(renamed.Content, "upProxy=sub_office_a") {
		t.Fatal("managed rename broke chain references")
	}
	removed, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a"}}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(removed.Content, "Remove=true") || len(removed.Selections) != 0 {
		t.Fatal("unreferenced managed upstream was not soft-removed")
	}
	queried, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: removed.Content})
	if err != nil || len(queried.Selections) != 0 {
		t.Fatalf("disabled node appeared selected: %v", err)
	}
	restored, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: removed.Content, Imports: []subscriptionImport{sampleSubscriptionImport()}})
	if err != nil || len(restored.Selections) != 1 || !strings.Contains(restored.Content, "Remove=false") {
		t.Fatalf("reselection failed to restore node: %v", err)
	}
}

func TestSubscriptionTOMLRejectsAmbiguousSources(t *testing.T) {
	_, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: "[[UpStreams]]\nName='sub_mysub_a'\nProxyUrl='http://127.0.0.1:9999'\n", Sources: []subscriptionSource{{SourceID: "other", Prefix: "sub_mysub_"}}, Imports: []subscriptionImport{sampleSubscriptionImport()}})
	if err == nil {
		t.Fatal("ambiguous subscription names were accepted")
	}
}

func TestSubscriptionOwnershipSurvivesVisualSave(t *testing.T) {
	imported := sampleSubscriptionImport()
	initial, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(initial.Content, "\n")
	for i, line := range lines {
		if strings.HasPrefix(line, "[[UpStreams]]") {
			lines[i] = "[[UpStreams]]"
		}
	}
	draft := strings.Replace(strings.Join(lines, "\n"), "Sleep=80", "Sleep=17", 1)
	retained, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: draft, RetainOwnershipFrom: initial.Content})
	if err != nil || len(retained.Selections) != 1 {
		t.Fatalf("ownership lost: %v", err)
	}
	imported.Upstream.Sleep = 120
	updated, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: retained.Content, Imports: []subscriptionImport{imported}})
	if err != nil || !strings.Contains(updated.Content, "Sleep=120") {
		t.Fatalf("unlocked visual edit was not updated after refresh: %v", err)
	}
}

func TestSubscriptionTOMLURLRefreshPreservesOnlyLockedQuery(t *testing.T) {
	imported := sampleSubscriptionImport()
	imported.Upstream.ProxyUrl = "http://127.0.0.1:8080?upProxy=front"
	imported.Upstream.Credit = -100
	base := "[[UpStreams]]\nName='front'\nProxyUrl='socks5://127.0.0.1:1080'\n"
	result, err := editTestSubscriptionTOML(subscriptionTOMLRequest{Content: base, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	custom := strings.Replace(result.Content, "http://127.0.0.1:8080?upProxy=front", "http://127.0.0.1:9000?credit=999&extra=yes", 1)
	imported.LockedFields = []string{"Credit", "UpProxy"}
	result, err = editTestSubscriptionTOML(subscriptionTOMLRequest{Content: custom, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	var config ServerConfig
	if _, err := btoml.Decode(result.Content, &config); err != nil {
		t.Fatal(err)
	}
	url := config.UpStreams[1].ProxyUrl
	if !strings.Contains(url, "credit=999") || strings.Contains(url, "9000") || strings.Contains(url, "upProxy=front") || strings.Contains(url, "extra=yes") {
		t.Fatalf("custom URL locks failed: %s", url)
	}
}
