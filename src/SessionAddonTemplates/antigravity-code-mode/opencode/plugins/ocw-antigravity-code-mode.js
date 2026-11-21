import { Plugin } from "@opencode/plugin"
import { Rpc } from "@opencode/plugin/rpc"

const providerID = "antigravity-acp"
const state = {
  type: "object",
  properties: { codeModeEnabled: { type: "boolean" } },
  required: ["codeModeEnabled"],
}

// Use the two methods we need from opencode-antigravity-acp@0.2.0.
const AcpRpc = Rpc.define({
  id: providerID,
  methods: {
    getState: {
      input: {
        type: "object",
        properties: { sessionID: { type: "string" } },
        required: ["sessionID"],
      },
      output: state,
    },
    setCodeMode: {
      input: {
        type: "object",
        properties: {
          sessionID: { type: "string" },
          codeModeEnabled: { type: "boolean" },
        },
        required: ["sessionID", "codeModeEnabled"],
      },
      output: state,
    },
  },
  events: {},
})

export default Plugin.define({
  id: "ocw.antigravity-code-mode",
  async setup(ctx) {
    const acp = ctx.rpc(AcpRpc)
    const initialized = new Set()
    await ctx.session.hook("model.request", async ({ sessionID, kind }) => {
      if (kind !== "primary" || initialized.has(sessionID)) return

      // Set the default once, before the first ACP turn. Later manual toggles still work.
      if (!(await acp.getState({ sessionID })).codeModeEnabled) {
        await acp.setCodeMode({ sessionID, codeModeEnabled: true })
      }
      initialized.add(sessionID)
    }, { providerID })
  },
})
