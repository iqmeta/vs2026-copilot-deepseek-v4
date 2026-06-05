"""3-round benchmark for all duplicated models (excluding deepseek-v4-pro)"""
import json, time, urllib.request, sys

PROXY_URL = "http://localhost:11434"

def test_model(model_name, timeout=120):
    body = json.dumps({
        "model": model_name,
        "messages": [{"role": "user", "content": "Say the word 'pong' only"}],
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

groups = {
    "deepseek-v4-flash": [
        ("deepseek-v4-flash@deepseek", "deepseek"),
        ("deepseek-v4-flash@ollama", "ollama"),
    ],
    "qwen3-coder-480b": [
        ("qwen/qwen3-coder-480b-a35b-instruct@nvidia", "nvidia"),
        ("qwen3-coder:480b@ollama", "ollama"),
    ],
    "qwen3.5-397b": [
        ("qwen/qwen3.5-397b-a17b@nvidia", "nvidia"),
        ("qwen3.5:397b@ollama", "ollama"),
    ],
    "nemotron-3-super": [
        ("nvidia/nemotron-3-super-120b-a12b@nvidia", "nvidia"),
        ("nemotron-3-super@ollama", "ollama"),
    ],
    "kimi-k2.6": [
        ("kimi-k2.6@moonshot", "moonshot"),
        ("kimi-k2.6@ollama", "ollama"),
    ],
    "gpt-oss-120b": [
        ("openai/gpt-oss-120b@groq", "groq"),
        ("gpt-oss-120b@cerebras", "cerebras"),
    ],
}

# Check which models exist
print("Checking available models...")
req = urllib.request.Request(f"{PROXY_URL}/api/tags")
d = json.loads(urllib.request.urlopen(req).read())
available = {m['name'] for m in d['models']}

for group_name, providers in groups.items():
    print(f"\n{'='*80}")
    print(f"  GROUP: {group_name}")
    print(f"{'='*80}")
    
    results = []
    for qname, provider in providers:
        if qname not in available:
            print(f"  {qname} ({provider}): SKIP (not in available models)")
            continue
        
        print(f"\n  --- {qname} (via {provider}) ---")
        times = []
        for r in range(1, 4):
            latency, result, err = test_model(qname, timeout=180)
            if latency > 0:
                times.append(latency)
                print(f"    Round {r}: {latency}ms - {result[:20]}")
            else:
                print(f"    Round {r}: FAIL - {err}: {result}")
        
        if times:
            avg = sum(times) / len(times)
            min_t = min(times)
            max_t = max(times)
            print(f"    >> AVG: {avg:.0f}ms (min: {min_t}, max: {max_t})")
            results.append((provider, avg, min_t, max_t, times))
        else:
            print(f"    >> All rounds failed")
            results.append((provider, 999999, 0, 0, []))
    
    # Winner
    valid = [r for r in results if r[1] < 999999]
    if valid:
        valid.sort(key=lambda x: x[1])
        winner = valid[0]
        print(f"\n  >> WINNER: {winner[0]} ({winner[1]:.0f}ms avg)")
        for r in valid[1:]:
            ratio = r[1] / winner[1] if winner[1] > 0 else 99
            print(f"  >> LOSER:  {r[0]} ({r[1]:.0f}ms avg, {ratio:.1f}x slower)")
    else:
        print(f"\n  >> No valid results")

print(f"\n{'='*80}")
print("  BENCHMARK COMPLETE")
print(f"{'='*80}")