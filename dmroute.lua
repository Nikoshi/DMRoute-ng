local p_dmroute = Proto("dmroute", "DMRoute-ng Mesh Protocol")

local f_magic = ProtoField.string("dmroute.magic", "Magic Header")
local f_zone = ProtoField.int32("dmroute.zone", "Zone ID", base.DEC)
local f_port = ProtoField.uint16("dmroute.port", "Data Port", base.DEC)
local f_device = ProtoField.int32("dmroute.device", "Device ID", base.DEC)
local f_ticks = ProtoField.int64("dmroute.ticks", "Ticks (UTC)", base.DEC)
local f_hmac = ProtoField.bytes("dmroute.hmac", "HMAC-SHA256")

p_dmroute.fields = { f_magic, f_zone, f_port, f_device, f_ticks, f_hmac }

function p_dmroute.dissector(buffer, pinfo, tree)
    local length = buffer:len()
    if length < 4 then return end

    local magic = buffer(0, 4):string()
    
    if magic == "DMBC" and length == 50 then
        pinfo.cols.protocol = "DMRoute Mesh"
        pinfo.cols.info = "DMBC (Beacon) - Zone: " .. buffer(4, 4):int()

        local subtree = tree:add(p_dmroute, buffer(), "DMRoute-ng Beacon (DMBC)")
        subtree:add(f_magic, buffer(0, 4))
        subtree:add(f_zone, buffer(4, 4))
        subtree:add(f_port, buffer(8, 2))
        subtree:add(f_ticks, buffer(10, 8))
        subtree:add(f_hmac, buffer(18, 32))
        
    elseif magic == "ROAM" and length == 52 then
        pinfo.cols.protocol = "DMRoute Mesh"
        pinfo.cols.info = "ROAM (Update) - Device: " .. buffer(4, 4):int() .. " -> Zone: " .. buffer(8, 4):int()

        local subtree = tree:add(p_dmroute, buffer(), "DMRoute-ng Roaming Update (ROAM)")
        subtree:add(f_magic, buffer(0, 4))
        subtree:add(f_device, buffer(4, 4))
        subtree:add(f_zone, buffer(8, 4))
        subtree:add(f_ticks, buffer(12, 8))
        subtree:add(f_hmac, buffer(20, 32))
    end
end

local udp_port = DissectorTable.get("udp.port")
udp_port:add(42069, p_dmroute)

-- --- MMDVM Homebrew Protocol (Port 62031) ---

local p_homebrew = Proto("homebrew", "MMDVM Homebrew Protocol")

local f_hb_magic = ProtoField.string("homebrew.magic", "Magic Header")
local f_hb_src = ProtoField.uint24("homebrew.src_id", "Source ID", base.DEC)
local f_hb_dst = ProtoField.uint24("homebrew.dst_id", "Destination ID", base.DEC)
local f_hb_rep = ProtoField.uint32("homebrew.repeater_id", "Repeater ID", base.DEC)

p_homebrew.fields = { f_hb_magic, f_hb_src, f_hb_dst, f_hb_rep }

function p_homebrew.dissector(buffer, pinfo, tree)
    local length = buffer:len()
    if length < 4 then return end

    local magic = buffer(0, 4):string()

    if magic == "DMRD" and length >= 23 then
        pinfo.cols.protocol = "Homebrew"
        
        local src = buffer(5, 3):uint()
        local dst = buffer(8, 3):uint()
        local rep = buffer(11, 4):uint()
        
        -- Byte 15 enthält die Bits für UnitCall/GroupCall und den Datentyp
        local bits = buffer(15, 1):uint()
        local data_type = bits % 16 -- Low Nibble
        
        local type_str = "Data/Voice"
        if data_type == 1 then type_str = "Header"
        elseif data_type == 2 then type_str = "Terminator"
        elseif data_type == 3 then type_str = "CSBK" end

        pinfo.cols.info = string.format("DMRD %s | Src: %d -> Dst: %d", type_str, src, dst)

        local subtree = tree:add(p_homebrew, buffer(), "DMR Data (DMRD)")
        subtree:add(f_hb_magic, buffer(0, 4))
        subtree:add(f_hb_src, buffer(5, 3))
        subtree:add(f_hb_dst, buffer(8, 3))
        subtree:add(f_hb_rep, buffer(11, 4))
        
    elseif magic == "RPTL" or magic == "RPTK" or magic == "RPTC" or magic == "DMRC" or magic == "RPTP" or magic == "MSTP" or magic == "MSTN" then
        pinfo.cols.protocol = "Homebrew"
        
        local full_magic = magic
        if length >= 7 and buffer(0, 7):string() == "RPTPING" then full_magic = "RPTPING"
        elseif length >= 7 and buffer(0, 7):string() == "MSTPONG" then full_magic = "MSTPONG"
        elseif length >= 6 and buffer(0, 6):string() == "RPTACK" then full_magic = "RPTACK"
        elseif length >= 6 and buffer(0, 6):string() == "MSTNAK" then full_magic = "MSTNAK"
        end
        
        pinfo.cols.info = full_magic
        local subtree = tree:add(p_homebrew, buffer(), "Control: " .. full_magic)
        subtree:add(f_hb_magic, buffer(0, 4))
        
        -- Repeater ID parsen, falls vorhanden
        if full_magic == "RPTPING" or full_magic == "MSTPONG" then
            if length >= 11 then subtree:add(f_hb_rep, buffer(7, 4)) end
        elseif length >= 8 then
            subtree:add(f_hb_rep, buffer(4, 4))
        end
    end
end

local udp_port = DissectorTable.get("udp.port")
udp_port:add(62031, p_homebrew)