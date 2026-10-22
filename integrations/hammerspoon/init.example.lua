-- Optional integration: review and merge this into your own init.lua.
-- Do not replace an existing configuration or shutdown callback wholesale.
local configPath = hs.configdir .. "/workspace_layouts.json"
local file = assert(io.open(configPath, "r"),
    "Copy workspace_layouts.example.json to workspace_layouts.json and set your display UUIDs first")
local data = file:read("*a")
file:close()
local config = hs.json.decode(data)
assert(type(config) == "table" and type(config.roles) == "table", "Invalid layout configuration")
for role, uuid in pairs(config.roles) do
    assert(type(uuid) == "string" and uuid ~= "" and not uuid:find("REPLACE_WITH_", 1, true),
        "Configure the local display UUID for role: " .. tostring(role))
end
assert(hs.fs.attributes(hs.configdir .. "/helpers/menu-bar-inset", "mode") == "file",
    "Build helpers/menu-bar-inset from the included Swift source first")

wm = require("window_manager")
workspaceLayouts = require("workspace_layouts")
assert(workspaceLayouts.start(wm, config))
wm.workspaceLayouts = workspaceLayouts
wm.start()

-- The module's compatibility bindings use Ctrl+Alt+Cmd+Shift. X/C/L use
-- private F17/F18/F19 with the existing T90 remapper. These extra bindings
-- also make the same actions directly usable without that external remapper.
local modifiers = { "ctrl", "alt", "cmd", "shift" }
local extraHotkeys = {}
for key, action in pairs({ x="workspaceTerminal", c="workspaceOffice", l="portraitMaximize" }) do
    local requested = action
    extraHotkeys[#extraHotkeys + 1] = hs.hotkey.bind(modifiers, key,
        function() wm.dispatch(requested) end)
end

local previousShutdown = hs.shutdownCallback
hs.shutdownCallback = function()
    workspaceLayouts.stop()
    wm.stop()
    for _, hotkey in ipairs(extraHotkeys) do hotkey:delete() end
    if previousShutdown then previousShutdown() end
end
