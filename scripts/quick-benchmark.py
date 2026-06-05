"""Quick benchmark for key duplicated models across providers."""
import json, time, urllib.request, sys

PROXY_URL = "http://localhost:11434"

def test_model(model_name):
    """Test a model with a simple ping. Returns latency in ms or -1 on failure."""
    body = json.dumps({
        "model": model_name,
        "messages": [{"role": "user", "content": "Respond with the word 'pong'"}],
        "stream": False,
        "options": {"num_predict": 1}
    }).encode()

    req = urllib.request.Request(f"{PROXY_URL}/api/chat", data=body,
                                 headers={"Content-Type": "application/json"},
                                 method="POST")

    try:
        start = time.time()
        resp = urllib.request.urlopen(req, timeout=60)
        elapsed = (time.time() - start) * 1000
        data = json.loads(resp.read())
        content = data.get("message", {}).get("content", "")
        return round(elapsed), content
    except Exception as e:
        return -1, str(e)

# Duplicate pairs to test
duplicates = [
    ("deepseek-v4-pro", ["deepseek-ai/deepseek-v4-pro", "deepseek-ai/deepseek-v4-pro@nvidia", "deepseek-v4-pro@ollama"]),
    ("deepseek-v4-flash", ["deepseek-v4-flash@deepseek", "deepseek-v4-flash@ollama"]),
    ("qwen3-coder-480b", ["qwen/qwen3-coder-480b-a35b-instruct@nvidia", "qwen3-coder:480b@ollama"]),
    ("qwen3.5-397b", ["qwen/qwen3.5-397b-a17b@nvidia", "qwen3.5:397b@ollama"]),
    ("nemotron-3-super", ["nvidia/nemotron-3-super-120b-a12b@nvidia", "nemotron-3-super@ollama"]),
    ("kimi-k2.6", ["kimi-k2.6@moonshot", "kimi-k2.6@ollama"]),
    ("glm-5.1", ["glm-5.1"]),
    ("minimax-m3", ["minimax-m3"]),
    ("qwen3-vl:235b", ["qwen3-vl:235b"]),
]

print("=" * 80)
print("  QUICK BENCHMARK - Duplicate Models")
print("=" * 80)
print("%-25s %-50s %-10s %s" % ("Model Group", "Qualified Name", "Latency", "Result"))
print("-" * 80)

results = []
for display, qualified_names in duplicates:
    print("\n" + "=" * 80)
    print("  [%s]" % display)

    for qname in qualified_names:
        latency, result = test_model(qname)
        result_text = result[:40] if latency > 0 else result[:60]
        print("  %-5s %-50s %-10s %s" % ("", qname, str(latency)+"ms" if latency > 0 else "ERR", result_text))
        results.append({"model": display, "qualified": qname, "latency_ms": latency, "success": latency > 0, "result": result_text})

print("\n" + "=" * 80)
print("  DUPLICATE ANALYSIS (fastest per group)")
print("=" * 80)

for key, group in __import__('itertools').groupby(sorted(results, key=lambda x: x["model"]), key=lambda x: x["model"]):
    items = list(group)
    good = [i for i in items if i["success"]]
    if len(good) <= 1:
        if len(good) == 1:
            print("\n  %s: %s (%sms) [only one provider]" % (key, good[0]["qualified"], good[0]["latency_ms"]))
        continue
    good.sort(key=lambda x: x["latency_ms"])
    fastest = good[0]
    print("\n  %s:" % key)
    print("    >> FASTEST: %s (%sms)" % (fastest["qualified"], fastest["latency_ms"]))
    for i in good[1:]:
        ratio = round(i["latency_ms"] / fastest["latency_ms"], 1) if fastest["latency_ms"] > 0 else 99
        print("    >> SLOWER (%sx): %s (%sms)" % (ratio, i["qualified"], i["latency_ms"]))