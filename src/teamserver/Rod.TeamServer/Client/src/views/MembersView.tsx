import { useCallback, useEffect, useState } from 'react'
import {
  type EngagementMember,
  type SessionOperator,
  addMember,
  listMembers,
  removeMember,
  setMemberRole,
} from '../api'
import { Icon } from '../components/Icons'

// The engagement's crew (architecture.md Sec 3, the membership model): the
// roster that decides reach. The owner row renders first -- access by
// creation, not by grant -- beside every member at their tier: a reader
// watches (every read and the live stream), a writer acts (tasking,
// listeners, closeout, the engagement's own management). Grant management is
// the owner's alone: invite by handle, re-tier, remove. Every change lands
// in the engagement's own audit trail, so the roster you see is the story
// the trail tells.
//
// Every other surface in this console rides the same roster: a reader sees a
// read-only mark in the engagement topbar and the server refuses her writes
// inline -- the membership panel is where the owner changes that.

const ROLE_HELP: Record<string, string> = {
  owner:
    'Access by creation, not by grant: full write access plus member management. Exactly one per engagement, bound when it was created, never removed or re-tiered.',
  writer:
    'The acting tier: every engagement-scoped write -- tasking, caught-shell interaction, listeners, payloads, closeout, deploy-token minting.',
  reader:
    'The watching tier: every engagement-scoped read and the live event stream. The server refuses her writes with 403; the mark explains what it already enforces.',
}

export function MembersView({
  engagementId,
  operator,
}: {
  engagementId: string
  operator: SessionOperator
}) {
  const [members, setMembers] = useState<EngagementMember[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  const [handle, setHandle] = useState('')
  const [role, setRole] = useState<'reader' | 'writer'>('writer')

  const ownerRow = members.find((m) => m.role === 'owner')
  const isOwner = ownerRow !== undefined && ownerRow.operatorId === operator.operatorId

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setMembers(await listMembers(engagementId))
      setError(null)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }, [engagementId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const onInvite = async (event: React.FormEvent) => {
    event.preventDefault()
    try {
      const added = await addMember(engagementId, handle.trim(), role)
      setNote(`Granted '${added.handle}' the ${role} tier -- it holds from this row on.`)
      setError(null)
      setHandle('')
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  const onSetRole = async (member: EngagementMember, next: 'reader' | 'writer') => {
    try {
      await setMemberRole(engagementId, member.operatorId, next)
      setNote(
        `'${member.handle}' now holds the ${next} tier -- live sessions carry it at their next request.`,
      )
      setError(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  const onRemove = async (member: EngagementMember) => {
    try {
      await removeMember(engagementId, member.operatorId)
      setNote(
        `Removed '${member.handle}' -- from its next request on, the engagement is invisible to it again.`,
      )
      setError(null)
      await refresh()
    } catch (e) {
      setNote(null)
      setError(String(e))
    }
  }

  return (
    <div className="card">
      <h3>
        <Icon name="users" />
        Members
      </h3>
      <p className="muted">
        The crew that decides reach: a reader watches, a writer acts, the owner manages this roster.
        Every grant, re-tier, and removal lands in the engagement's audit trail.
      </p>

      {isOwner ? (
        <form className="build-form" onSubmit={onInvite}>
          <fieldset>
            <legend>Invite an operator</legend>
            <label>
              Handle
              <input
                value={handle}
                onChange={(e) => setHandle(e.target.value)}
                placeholder="alice"
                title="The account's login handle (provisioned in Settings). The invite names a person, not a role."
                required
              />
            </label>
            <label>
              Tier
              <select
                value={role}
                onChange={(e) => setRole(e.target.value as 'reader' | 'writer')}
                title={ROLE_HELP.writer + ' ' + ROLE_HELP.reader}
              >
                <option value="writer">writer -- act and manage</option>
                <option value="reader">reader -- watch only</option>
              </select>
            </label>
          </fieldset>
          <button className="primary" type="submit" disabled={busy}>
            Grant access
          </button>
        </form>
      ) : (
        <p className="muted">
          Grant management is the owner's alone -- you hold the{' '}
          {members.find((m) => m.operatorId === operator.operatorId)?.role ?? 'member'} tier on this
          engagement.
        </p>
      )}
      {note && <p className="muted">{note}</p>}
      {error && <p className="error">{error}</p>}

      <div className="inline-form">
        <button className="ghost" onClick={() => void refresh()} disabled={busy}>
          <Icon name="refresh" />
          Refresh
        </button>
      </div>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Handle</th>
              <th>Display name</th>
              <th>Tier</th>
              <th>Granted</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {members.length === 0 && (
              <tr>
                <td colSpan={5}>
                  <div className="empty">
                    <Icon name="users" />
                    The roster did not load -- refresh to try again.
                  </div>
                </td>
              </tr>
            )}
            {members.map((member) => (
              <tr key={member.operatorId}>
                <td>
                  <span className="endpoint-cell">
                    <code>{member.handle}</code>
                    {member.operatorId === operator.operatorId && <span className="muted"> (you)</span>}
                  </span>
                </td>
                <td>{member.displayName}</td>
                <td title={ROLE_HELP[member.role] ?? member.role}>
                  {member.role}
                </td>
                <td title={new Date(member.addedAt).toLocaleString()}>
                  {new Date(member.addedAt).toLocaleDateString()}
                </td>
                <td>
                  {isOwner && member.role !== 'owner' && (
                    <span className="row-actions">
                      <button
                        className="ghost sm"
                        onClick={() =>
                          void onSetRole(member, member.role === 'writer' ? 'reader' : 'writer')
                        }
                        title={`Move to the ${member.role === 'writer' ? 'reader (watch only)' : 'writer (act and manage)'} tier. Takes effect on the member's next request.`}
                      >
                        Make {member.role === 'writer' ? 'reader' : 'writer'}
                      </button>
                      <button
                        className="ghost sm"
                        onClick={() => void onRemove(member)}
                        title="Remove this membership. From its next request on, the operator cannot even see the engagement exists. The row can be re-granted at any time."
                      >
                        Remove
                      </button>
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
