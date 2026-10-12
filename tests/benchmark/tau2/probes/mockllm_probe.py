import json
import urllib.request

url = 'http://localhost:5098/v1/chat/completions'

# Test 1: Request with tool_calls in history + tool result
data1 = {
    'model': 'mock',
    'messages': [
        {'role': 'user', 'content': 'Hello'},
        {'role': 'assistant', 'content': None, 'tool_calls': [{'id': 'call_1', 'type': 'function', 'function': {'name': 'get_order_details', 'arguments': '{"order_id": "#W5272531"}'}}]},
        {'role': 'tool', 'tool_call_id': 'call_1', 'content': '{"order_id": "#W5272531", "status": "delivered"}'}
    ]
}
print("=== Test 1: tool_calls + tool result in history ===")
req = urllib.request.Request(url, data=json.dumps(data1).encode(), headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(req) as resp:
    print(resp.read().decode())

# Test 2: Request with tools parameter (scenario harness-tool)
data2 = {
    'model': 'mock',
    'messages': [{'role': 'user', 'content': 'Calculate something'}],
    'tools': [{'type': 'function', 'function': {'name': 'calculator', 'description': 'Calculate', 'parameters': {'type': 'object', 'properties': {'expression': {'type': 'string'}}}} }]
}
print("\n=== Test 2: with tools parameter (harness-tool scenario) ===")
# First set scenario
req_scenario = urllib.request.Request('http://localhost:5098/control/scenario?name=harness-tool', method='POST')
with urllib.request.urlopen(req_scenario) as resp:
    print('Scenario set:', resp.read().decode())

req = urllib.request.Request(url, data=json.dumps(data2).encode(), headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(req) as resp:
    print(resp.read().decode())

# Test 3: Request with tools parameter (reject-tools scenario)
print("\n=== Test 3: reject-tools scenario ===")
req_scenario = urllib.request.Request('http://localhost:5098/control/scenario?name=reject-tools', method='POST')
with urllib.request.urlopen(req_scenario) as resp:
    print('Scenario set:', resp.read().decode())

req = urllib.request.Request(url, data=json.dumps(data2).encode(), headers={'Content-Type': 'application/json'})
try:
    with urllib.request.urlopen(req) as resp:
        print(resp.read().decode())
except urllib.error.HTTPError as e:
    print(f'HTTP {e.code}: {e.read().decode()}')

# Test 4: streaming
print("\n=== Test 4: streaming ===")
req_scenario = urllib.request.Request('http://localhost:5098/control/scenario?name=normal', method='POST')
with urllib.request.urlopen(req_scenario) as resp:
    print('Scenario set:', resp.read().decode())

data3 = {
    'model': 'mock',
    'messages': [{'role': 'user', 'content': 'Hello'}],
    'stream': True
}
req = urllib.request.Request(url, data=json.dumps(data3).encode(), headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(req) as resp:
    for line in resp:
        line = line.decode().strip()
        if line:
            print(line)