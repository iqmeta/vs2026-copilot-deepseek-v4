"""Final benchmark: 3 rounds for deepseek-v4-pro across all providers"""
import json, time, urllib.request, sys

PROXY_URL = "http://localhost:11434"

def test_model(model_name, timeout=60):
    body = json.dumps({
        "model": model_name,
        "messages": [{"role": "user", "content": "Respond with the word 'pong'"}],
        "stream": False,
        "options": {"num_predict": 1}
    }).encode()
    req = urllib.request.Request(f"{PROXY_URL}/api/chat", data=body,
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        start = time.time()
        resp = urllib.request.urlopen(req, timeout=timeout)
        elapsed = (time.time() - start) * 1000
        data = json.loads(resp.read())
        content = data.get("message", {}).get("content", "")
        return round(elapsed), content, None
    except Exception as e:
        return -1, str(e)[:80], type(e).__name__

providers = [
    ("deepseek-ai/deepseek-v4-pro", "deepseek"),
    ("deepseek-ai/deepseek-v4-pro@nvidia", "nvidia"),
    ("deepseek-v4-pro@ollama", "ollama"),
]

print("=" * 80)
print("  FINAL BENCHMARK: deepseek-v4-pro (3 rounds per provider)")
print("=" * 80)

for qname, provider in providers:
    print("\n--- %s (via %s) ---" % (qname, provider))
    times = []
    for r in range(1, 4):
        latency, result, err = test_model(qname)
        if latency > 0:
            times.append(latency)
            print("  Round %d: %dms - OK" % (r, latency))
        else:
            print("  Round %d: FAIL - %s (%s)" % (r, result, err))
    
    if times:
        avg = sum(times) / len(times)
        print("  >> AVG: %dms (min: %d, max: %d)" % (avg, min(times), max(times)))
    else:
        print("  >> All rounds failed")

print("\n" + "=" * 80)
print("  WINNER ANALYSIS")
print("=" * 80)