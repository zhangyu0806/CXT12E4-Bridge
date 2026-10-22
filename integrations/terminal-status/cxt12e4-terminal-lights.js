import { homedir } from "node:os"
import { join } from "node:path"

export const Cxt12E4TerminalLightsPlugin = async ({ $ }) => {
  const reporter = join(homedir(), ".local", "bin", "cxt12e4-agent-status")
  const states = new Map()
  let lastState = ""

  const publish = async (state) => {
    if (state === lastState) return
    lastState = state
    await $`${reporter} ${state}`.quiet().nothrow()
  }

  const aggregate = () => {
    const values = Array.from(states.values())
    if (values.includes("error")) return "error"
    if (values.includes("waiting")) return "waiting"
    if (values.includes("thinking")) return "thinking"
    if (values.includes("completed")) return "completed"
    return "idle"
  }

  await publish("idle")

  return {
    event: async ({ event }) => {
      const properties = event.properties ?? {}
      const sessionID = properties.sessionID ?? "current"

      if (event.type === "session.status") {
        const kind = properties.status?.type
        if (kind === "busy" || kind === "retry") {
          for (const [id, value] of states) {
            if (value === "completed" || value === "error") states.set(id, "idle")
          }
          states.set(sessionID, "thinking")
        } else if (kind === "idle") {
          states.set(sessionID, "completed")
        }
      } else if (event.type === "session.idle") {
        states.set(sessionID, "completed")
      } else if (event.type === "permission.asked" || event.type === "question.asked") {
        states.set(sessionID, "waiting")
      } else if (event.type === "permission.replied" || event.type === "question.replied") {
        states.set(sessionID, "thinking")
      } else if (event.type === "session.error") {
        states.set(sessionID, "error")
      } else if (event.type === "session.deleted") {
        states.delete(sessionID)
      } else {
        return
      }

      await publish(aggregate())
    },
  }
}
