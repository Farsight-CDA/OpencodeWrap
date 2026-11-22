import { Plugin } from "@opencode/plugin"
import { Rpc } from "@opencode/plugin/rpc"

const providerID = "antigravity-acp"
const mode = { type: "string", enum: ["auto", "accept-edits", "default", "plan"] }
const state = { type: "object", properties: { mode }, required: ["mode"] }

// Use the mode methods from opencode-antigravity-acp@0.2.0.
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
    setMode: {
      input: {
        type: "object",
        properties: { sessionID: { type: "string" }, mode },
        required: ["sessionID", "mode"],
      },
      output: state,
    },
  },
  events: {},
})

export default Plugin.define({
  id: "ocw.antigravity-execution",
  async setup(ctx) {
    const acp = ctx.rpc(AcpRpc)
    await ctx.session.hook("model.request", async ({ sessionID, kind }) => {
      if (kind !== "primary") return

      // Auto forces the nested sandbox. The launcher already sets ACP to always proceed.
      // Accept edits is sent explicitly to AGY even on a fresh or resumed ACP session.
      if ((await acp.getState({ sessionID })).mode === "auto") {
        await acp.setMode({ sessionID, mode: "accept-edits" })
      }
    }, { providerID })
  },
})
