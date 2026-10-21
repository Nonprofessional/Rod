// The UI half of the contract-trilogy acceptance (architecture.md Sec
// 15.2): the generic form builder renders a schema the framework never
// anticipated and serializes the operator's values into the verb's opaque
// argument string -- the same build the server-side test issues -- with the
// file arm's inline/staged split and the trailing remainder rule. If a novel
// module's schema renders wrong here, the module earned a broken form; if it
// renders right, the UI needed zero changes to task it.

import { describe, expect, it } from 'vitest'
import type { CapabilityDescriptor, VerbParameter } from './api'
import { buildPathUpload, DEFAULT_TASK_FORM, formForDescriptor, type SelectedFile } from './verbForms'

function descriptor(parameters: VerbParameter[] | null): CapabilityDescriptor {
  return {
    verb: 'sweep.discover',
    category: 'Recon',
    version: '0.3',
    attributes: {},
    parameters,
    output: null,
  }
}

const param = (over: Partial<VerbParameter> & Pick<VerbParameter, 'name' | 'label' | 'kind'>): VerbParameter => ({
  required: false,
  help: null,
  placeholder: null,
  choices: null,
  trailing: false,
  inline: true,
  ...over,
})

describe('formForDescriptor', () => {
  it('renders a novel schema into fields in declaration order', () => {
    const form = formForDescriptor(
      descriptor([
        param({ name: 'host', label: 'Host', kind: 'String', required: true }),
        param({ name: 'range', label: 'Port range', kind: 'String', required: true }),
      ]),
    )

    expect(form.fields.map((f) => f.key)).toEqual(['host', 'range'])
    expect(form.fields[0].required).toBe(true)
  })

  it('serializes space-joined tokens in declaration order, empty optionals dropped', async () => {
    const form = formForDescriptor(
      descriptor([
        param({ name: 'sleep', label: 'Contact every', kind: 'String', required: true }),
        param({ name: 'jitter', label: 'Randomize', kind: 'String' }),
      ]),
    )

    const built = await form.build({ sleep: ' 30s ', jitter: '' }, {})
    expect(built.arguments).toBe('30s')
    expect(built.content).toBeUndefined()
  })

  it('passes a trailing parameter through raw after the joined tokens', async () => {
    const form = formForDescriptor(
      descriptor([
        param({ name: 'mechanism', label: 'Mechanism', kind: 'Enum', required: true, choices: ['cron', 'systemd'] }),
        param({ name: 'name', label: 'Name', kind: 'String', required: true }),
        param({ name: 'payload', label: 'Payload', kind: 'Text', required: true, trailing: true }),
      ]),
    )

    const built = await form.build(
      { mechanism: 'cron', name: 'update-service', payload: '/usr/bin/touch /tmp/pwn every hour' },
      {},
    )
    expect(built.arguments).toBe('cron update-service /usr/bin/touch /tmp/pwn every hour')
  })

  it('serializes booleans as true when checked and drops them when not', async () => {
    const form = formForDescriptor(
      descriptor([
        param({ name: 'host', label: 'Host', kind: 'String', required: true }),
        param({ name: 'verbose', label: 'Verbose', kind: 'Boolean' }),
      ]),
    )

    expect((await form.build({ host: 'h', verbose: true }, {})).arguments).toBe('h true')
    expect((await form.build({ host: 'h', verbose: false }, {})).arguments).toBe('h')
  })

  it('appends inline file bytes as the tail token under the ceiling', async () => {
    const form = formForDescriptor(
      descriptor([
        param({ name: 'file', label: 'Local file', kind: 'File', required: true }),
        param({ name: 'path', label: 'Remote path', kind: 'String', required: true }),
      ]),
    )
    const file: SelectedFile = { name: 'note.txt', base64: 'cmlkIHVuaXQ=', bytes: 9 }

    const built = await form.build({ path: '/tmp/note.txt' }, { file })
    expect(built.arguments).toBe('/tmp/note.txt cmlkIHVuaXQ=')
    expect(built.content).toBeUndefined()
  })

  it('rides the staged arm above the ceiling and for staged-only verbs, without hashing', async () => {
    // The server appends the sha256 to the signed arguments when Content is
    // present (Sec 10's typed arm), so the client never hashes -- the
    // arguments carry the tokens alone and the bytes ride Content.
    const large: SelectedFile = { name: 'tool.bin', base64: 'QQ==', bytes: 2 * 1024 * 1024 }

    const inlineCapable = formForDescriptor(
      descriptor([
        param({ name: 'file', label: 'File', kind: 'File', required: true }),
        param({ name: 'path', label: 'Path', kind: 'String', required: true }),
      ]),
    )
    const staged = await inlineCapable.build({ path: '/tmp/tool.bin' }, { file: large })
    expect(staged.arguments).toBe('/tmp/tool.bin')
    expect(staged.content).toBe('QQ==')

    const stagedOnly = formForDescriptor(
      descriptor([
        param({ name: 'name', label: 'Module', kind: 'String', required: true }),
        param({ name: 'file', label: 'Binary', kind: 'File', required: true, inline: false }),
      ]),
    )
    const moduleLoad = await stagedOnly.build({ name: 'hostenum' }, { file: { name: 'hostenum', base64: 'QUI=', bytes: 4 } })
    expect(moduleLoad.arguments).toBe('hostenum')
    expect(moduleLoad.content).toBe('QUI=')
  })

  it('falls back to the raw-argument form for a verb with no schema', () => {
    const form = formForDescriptor(descriptor(null))

    expect(form.fields).toEqual(DEFAULT_TASK_FORM.fields)
    expect(form.fields[0].key).toBe('args')
  })

  it('builds the browser upload shape with the shared file arm', async () => {
    const inline = buildPathUpload('/tmp/x', { name: 'x', base64: 'Yg==', bytes: 1 })
    expect(inline).toEqual({ arguments: '/tmp/x Yg==' })

    const staged = buildPathUpload('/tmp/x', { name: 'x', base64: 'Yg==', bytes: 2 * 1024 * 1024 })
    expect(staged).toEqual({ arguments: '/tmp/x', content: 'Yg==' })
  })
})
