import {
    closeSync, constants, fstatSync, linkSync, lstatSync, mkdirSync, mkdtempSync, openSync, readFileSync,
    readdirSync, renameSync, rmSync, writeFileSync,
} from "node:fs"
import { dirname, join } from "node:path"

const [profileRoot, geminiHome] = process.argv.slice(2)
const sessionManifest = join(dirname(profileRoot), "antigravity-skills.json")

function* findSkills(directory, depth = 0) {
    const files = readdirSync(directory, { withFileTypes: true })
    if (depth > 0 && files.some((file) => file.name === "SKILL.md" && file.isFile())) {
        yield { path: directory }
        return
    }

    if (depth === 6) return
    for (const file of files.sort((a, b) => a.name.localeCompare(b.name, "en"))) {
        if (file.isDirectory() && !file.name.startsWith(".") && file.name !== "node_modules") {
            yield* findSkills(join(directory, file.name), depth + 1)
        }
    }
}

function writeManifest(path, value, mode = 0o600, createOnly = false) {
    const staging = mkdtempSync(join(dirname(path), ".ocw-skills-"))
    const temp = join(staging, "skills.json")
    try {
        writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, { mode })
        if (createOnly) {
            linkSync(temp, path)
        } else {
            renameSync(temp, path)
        }
    } finally {
        rmSync(staging, { recursive: true, force: true })
    }
}

function readManifest(path) {
    let fd
    try {
        fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK)
    } catch (error) {
        if (error.code === "ENOENT") return { value: {} }
        throw error
    }

    try {
        const info = fstatSync(fd)
        if (!info.isFile()) throw new Error("AGY skills.json is not a regular file.")
        let value
        try {
            value = JSON.parse(readFileSync(fd, "utf8"))
        } catch {
            throw new Error("AGY skills.json is not valid JSON.")
        }
        if (!value || typeof value !== "object" || Array.isArray(value)
            || (value.inherits !== undefined && !Array.isArray(value.inherits))) {
            throw new Error("AGY skills.json has an unexpected format.")
        }
        return { value, mode: info.mode & 0o777 }
    } finally {
        closeSync(fd)
    }
}

function registerManifest(path) {
    for (let attempt = 0; ; ++attempt) {
        const { value, mode } = readManifest(path)
        value.inherits ??= []
        if (value.inherits.some((entry) => entry?.path === sessionManifest)) return

        value.inherits.push({ path: sessionManifest })
        try {
            writeManifest(path, value, mode, mode === undefined)
            return
        } catch (error) {
            // A second AGY process can create the file after we read it.
            if (attempt !== 0 || mode !== undefined || error.code !== "EEXIST") throw error
        }
    }
}

try {
    const skillsRoot = join(profileRoot, "opencode", "skills")
    const entries = lstatSync(skillsRoot, { throwIfNoEntry: false })?.isDirectory() ? [...findSkills(skillsRoot)] : []
    // The persistent reference resolves to a different manifest in each container.
    // An empty manifest makes a skill disappear when its addon is not selected.
    writeManifest(sessionManifest, { entries })

    if (entries.length > 0) {
        const config = join(geminiHome, "config")
        mkdirSync(config, { recursive: true, mode: 0o700 })
        if (!lstatSync(config).isDirectory()) throw new Error("AGY config is not a regular directory.")
        registerManifest(join(config, "skills.json"))
    }
} catch (error) {
    console.error(`OCW: ${error.message}`)
    process.exitCode = 1
}
