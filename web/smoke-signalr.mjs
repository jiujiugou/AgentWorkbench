import * as signalR from '@microsoft/signalr'

const url = process.env.HUB_URL ?? 'http://localhost:8080/hubs/agent'
const text = process.env.CHAT_TEXT ?? 'hello, reply ok'

const conn = new signalR.HubConnectionBuilder().withUrl(url).withAutomaticReconnect().build()

let acc = ''
conn.on('ReceiveDelta', (p) => {
  acc += p.delta
})
conn.on('ReceiveSources', (p) => {
  console.log('[SOURCES]', (p.sources || []).length)
})
conn.on('ReceiveDone', () => {
  console.log('[ANSWER]', acc)
  process.exit(0)
})
conn.on('Error', (p) => {
  console.log('[ERROR]', p.message)
  process.exit(1)
})

await conn.start()
console.log('[connected]')
await conn.invoke('Send', { conversationId: 't', text, history: [] })
setTimeout(() => {
  console.log('[TIMEOUT]', acc)
  process.exit(2)
}, 90000)
