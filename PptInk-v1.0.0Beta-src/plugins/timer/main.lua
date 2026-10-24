-- ============================================================
-- 课堂正倒计时（PANEL 魔数=1 插件示例）
-- 宿主负责渲染面板元素；本脚本只做逻辑：
--   注入的沙箱 API：startTimer / stopTimers / updatePanelElement / showToast / closePanel
-- ============================================================

local running = false
local remain = 300          -- 秒

local function fmt(s)
    return string.format("%02d:%02d", math.floor(s / 60), s % 60)
end

function onActivate()
    -- 面板激活：同步一次初值
    updatePanelElement("display", fmt(remain))
end

function onDeactivate()
    stopTimers()
    running = false
end

-- 滑条：设定倒计时时长（10s ~ 60min）
function onDurationChange(v)
    remain = math.floor(tonumber(v) or 300)
    updatePanelElement("display", fmt(remain))
end

-- 按钮：开始 / 暂停
function onToggle()
    if running then
        stopTimers()
        running = false
    else
        startTimer(1.0, "onTick")
        running = true
    end
end

-- 宿主计时回调（每 1s）
function onTick()
    if remain > 0 then
        remain = remain - 1
        updatePanelElement("display", fmt(remain))
        if remain == 0 then
            stopTimers()
            running = false
            showToast("时间到！")
        end
    end
end
