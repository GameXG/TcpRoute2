package main

import (
	"reflect"
	"testing"
)

func TestDialProxyTargetSelectors(t *testing.T) {
	configs := []ServerConfigUpStream{
		{Name: "tw_0111"}, {Name: "tw_0da"}, {Name: "tw_0232_11aa"},
		{Name: "hk_01"}, {Name: "exact"}, {Name: "exact-other"},
		{Name: "literal,comma"}, {Name: "unrelated", Alias: "tw_0alias"},
	}
	byName := make(map[string]int, len(configs))
	for index, config := range configs {
		byName[config.Name] = index
	}
	for _, test := range []struct {
		value string
		want  []int
	}{
		{"tw_0", []int{0, 1, 2}},
		{"tw_0*", []int{0, 1, 2}},
		{"tw_0111,hk_", []int{0, 3}},
		{" tw_0 , tw_0111, hk_* ", []int{0, 1, 2, 3}},
		{"exact", []int{4}},
		{"exact*", []int{4, 5}},
		{"literal,comma", []int{6}},
	} {
		t.Run(test.value, func(t *testing.T) {
			got, err := dialProxyTargets(test.value, configs, byName)
			if err != nil || !reflect.DeepEqual(got, test.want) {
				t.Fatalf("got %v, %v; want %v", got, err, test.want)
			}
		})
	}
	for _, value := range []string{"", "*", " * ", "tw_0,*", "*,tw_0", "tw_0,", ",tw_0", "tw_0,,hk_", "tw_0,missing", "TW_0", "tw_0alias"} {
		if _, err := dialProxyTargets(value, configs, byName); err == nil {
			t.Fatalf("invalid selector %q was accepted", value)
		}
	}
}

func TestRenameDialProxyTargetSelectors(t *testing.T) {
	names := map[string]string{"sub_old_a": "sub_new_a", "literal,comma": "renamed"}
	prefixes := map[string]string{"sub_old_": "sub_new_"}
	for _, test := range []struct{ value, want string }{
		{"sub_old_a", "sub_new_a"},
		{"sub_old_", "sub_new_"},
		{"sub_old_*,sub_old_a,other", "sub_new_*,sub_new_a,other"},
		{"literal,comma", "renamed"},
		{"unrelated", "unrelated"},
	} {
		if got := renameDialProxyTargets(test.value, names, prefixes); got != test.want {
			t.Fatalf("rename %q = %q; want %q", test.value, got, test.want)
		}
	}
}
