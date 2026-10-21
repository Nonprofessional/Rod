// The UI half of the output-taxonomy acceptance (architecture.md Sec
// 15.2's second contract): the generic panels parse a declared kind's
// documented JSON-lines grammar over any verb's completed output, and output
// that does not parse degrades to plain text -- the UI keeps working, the
// module just earns no panels.

import { describe, expect, it } from 'vitest'
import { panelEarnsRender, renderOutput } from './outputRender'

describe('renderOutput', () => {
  it('parses process rows against the grammar', () => {
    const panel = renderOutput(
      'ProcessList',
      '{"pid":1,"ppid":0,"user":"root","image":"/sbin/init"}\n{"pid":42,"ppid":1,"user":"cw","image":"/usr/bin/rod"}',
    )

    expect(panel.kind).toBe('process-list')
    if (panel.kind !== 'process-list') return
    expect(panel.rows).toHaveLength(2)
    expect(panel.rows[1]).toEqual({ pid: 42, ppid: 1, user: 'cw', image: '/usr/bin/rod' })
  })

  it('parses file rows, credential rows, and enumeration findings', () => {
    const files = renderOutput('FileList', '{"name":"etc","dir":true,"size":4096}\n{"name":"hostname","dir":false,"size":7}')
    expect(files.kind).toBe('file-list')
    if (files.kind === 'file-list') expect(files.rows[0].dir).toBe(true)

    const creds = renderOutput(
      'Credential',
      '{"kind":"ssh","principal":"root","secret":"s","source":"id_ed25519"}\n{"kind":"browser","principal":"op@example"}',
    )
    expect(creds.kind).toBe('credential')
    if (creds.kind === 'credential') {
      expect(creds.rows).toHaveLength(2)
      expect(creds.rows[0].secret).toBe('s')
      expect(creds.rows[1].secret).toBeNull()
    }

    const sweep = renderOutput(
      'Enumeration',
      '{"host":"10.0.0.7","port":443,"state":"open","service":"https"}\n{"host":"db01","addresses":["10.0.0.9"],"os":"linux"}',
    )
    expect(sweep.kind).toBe('enumeration')
    if (sweep.kind === 'enumeration') {
      expect(sweep.rows[1].address).toBe('10.0.0.9')
      expect(sweep.rows[1].os).toBe('linux')
    }
  })

  it('degrades unparseable typed output to plain text', () => {
    // A module that typed its output poorly: the grammar cannot read it, so
    // the panel earns nothing and the transcript remains the read.
    const panel = renderOutput('ProcessList', 'pid=1 ppid=0 user=root image=/sbin/init\nprose')

    expect(panel.kind).toBe('process-list')
    expect(panelEarnsRender(panel)).toBe(false)
  })

  it('ignores lines that do not parse and rows that miss required fields', () => {
    const panel = renderOutput(
      'Enumeration',
      'not json\n{"port":22}\n{"host":"web01"}',
    )

    expect(panel.kind).toBe('enumeration')
    if (panel.kind !== 'enumeration') return
    expect(panel.rows).toEqual([
      { host: 'web01', port: null, state: null, service: null, address: null, os: null, arch: null },
    ])
  })

  it('treats artifact kinds as panels without an inline grammar', () => {
    expect(renderOutput('File', 'anything')).toEqual({ kind: 'file' })
    expect(renderOutput('Screenshot', '')).toEqual({ kind: 'screenshot' })
    expect(renderOutput(null, 'plain prose')).toEqual({ kind: 'text' })
  })
})
