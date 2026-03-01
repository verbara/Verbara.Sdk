#!/usr/bin/env bash
# ============================================================================
# generate-events.sh
# Generates C# POCO event files from asterisk-java Java source files.
# ============================================================================
set -euo pipefail

JAVA_DIR="/home/harol/Repositories/Sources/asterisk-java/src/main/java/org/asteriskjava/manager/event"
OUT_DIR="/home/harol/Repositories/Sources/Asterisk.NetAot/src/Asterisk.NetAot.Ami/Events"

# Files to skip (abstract bases, enum, special)
SKIP_FILES=(
    "ManagerEvent.java"
    "AbstractAgentEvent.java"
    "AbstractBridgeEvent.java"
    "AbstractChannelEvent.java"
    "AbstractChannelStateEvent.java"
    "AbstractChannelTalkingEvent.java"
    "AbstractConfbridgeEvent.java"
    "AbstractFaxEvent.java"
    "AbstractHoldEvent.java"
    "AbstractMeetMeEvent.java"
    "AbstractMixMonitorEvent.java"
    "AbstractMonitorEvent.java"
    "AbstractParkedCallEvent.java"
    "AbstractQueueMemberEvent.java"
    "AbstractRtcpEvent.java"
    "AbstractRtpStatEvent.java"
    "AbstractSecurityEvent.java"
    "AbstractUnParkedEvent.java"
    "ResponseEvent.java"
    "UserEvent.java"
    "ContactStatusEnum.java"
)

# ManagerEvent getters to skip (always inherited)
SKIP_GETTERS="getPrivilege|getUniqueId|getTimestamp|getSource|getDateReceived|getServer|getSystemName|getFile|getLine|getFunc|getSequenceNumber|getChannelVariables|getClass|getCallerIdName|getConnectedLineNum|getConnectedLineName|getPriority|getChannelState|getChannelStateDesc|getExten|getCallerIdNum|getContext"

# Additional getters to skip per base class (properties already in C# base)
# ChannelEventBase: Channel, ChannelState, ChannelStateDesc, CallerIdNum, CallerIdName, ConnectedLineNum, ConnectedLineName, AccountCode, Context, Exten, Priority, Language, Linkedid
CHANNEL_BASE_SKIP="getChannel|getChannelState|getChannelStateDesc|getCallerIdNum|getCallerIdName|getConnectedLineNum|getConnectedLineName|getAccountCode|getContext|getExten|getPriority|getLanguage|getLinkedid"

# BridgeEventBase: BridgeUniqueid, BridgeType, BridgeTechnology, BridgeCreator, BridgeName, BridgeNumChannels, BridgeVideoSourceMode
BRIDGE_BASE_SKIP="getBridgeUniqueid|getBridgeType|getBridgeTechnology|getBridgeCreator|getBridgeName|getBridgeNumChannels|getBridgeVideoSourceMode|getBridgevideosourcemode"

# QueueMemberEventBase: Queue, MemberName, Interface, StateInterface, Membership, Penalty, CallsTaken, Status, Paused, PausedReason, Ringinuse, LastCall, LastPause, InCall
QUEUEMEMBER_BASE_SKIP="getQueue|getMemberName|getInterface|getStateInterface|getStateinterface|getMembership|getPenalty|getCallsTaken|getStatus|getPaused|getPausedReason|getRinginuse|getLastCall|getLastPause|getInCall"

# AgentEventBase: Agent, Channel
AGENT_BASE_SKIP="getAgent|getChannel"

# ConfbridgeEventBase: Conference, BridgeUniqueid, Channel
CONFBRIDGE_BASE_SKIP="getConference|getBridgeUniqueid|getChannel"

# MeetMeEventBase: Meetme, Channel, Usernum
MEETME_BASE_SKIP="getMeetme|getChannel|getUsernum"

# SecurityEventBase: EventTV, Severity, Service, AccountID, SessionID, LocalAddress, RemoteAddress, SessionTV
SECURITY_BASE_SKIP="getEventTV|getEventtv|getSeverity|getService|getAccountID|getAccountId|getSessionID|getSessionId|getLocalAddress|getRemoteAddress|getSessionTV|getSessiontv|getEventVersion|getModule|getEvent"

# FaxEventBase: Channel, LocalStationID, RemoteStationID
FAX_BASE_SKIP="getChannel|getLocalStationID|getLocalStationId|getRemoteStationID|getRemoteStationId"

# ResponseEvent: ActionId
RESPONSE_BASE_SKIP="getActionId"

is_skipped() {
    local filename="$1"
    for skip in "${SKIP_FILES[@]}"; do
        if [[ "$filename" == "$skip" ]]; then
            return 0
        fi
    done
    return 1
}

# Determine base class from Java extends clause
get_base_class() {
    local extends_clause="$1"
    case "$extends_clause" in
        AbstractChannelEvent|AbstractChannelStateEvent|AbstractChannelTalkingEvent|AbstractHoldEvent|AbstractMixMonitorEvent|AbstractMonitorEvent|AbstractParkedCallEvent|AbstractUnParkedEvent)
            echo "ChannelEventBase"
            ;;
        AbstractBridgeEvent)
            echo "BridgeEventBase"
            ;;
        AbstractQueueMemberEvent)
            echo "QueueMemberEventBase"
            ;;
        AbstractAgentEvent)
            echo "AgentEventBase"
            ;;
        AbstractConfbridgeEvent)
            echo "ConfbridgeEventBase"
            ;;
        AbstractMeetMeEvent)
            echo "MeetMeEventBase"
            ;;
        AbstractSecurityEvent)
            echo "SecurityEventBase"
            ;;
        AbstractFaxEvent)
            echo "FaxEventBase"
            ;;
        AbstractRtcpEvent|AbstractRtpStatEvent)
            echo "ManagerEvent"
            ;;
        ResponseEvent)
            echo "ResponseEvent"
            ;;
        ManagerEvent|*)
            echo "ManagerEvent"
            ;;
    esac
}

# Get additional skip getters based on base class
get_base_skip_getters() {
    local base_class="$1"
    case "$base_class" in
        ChannelEventBase)
            echo "$CHANNEL_BASE_SKIP"
            ;;
        BridgeEventBase)
            echo "$BRIDGE_BASE_SKIP"
            ;;
        QueueMemberEventBase)
            echo "$QUEUEMEMBER_BASE_SKIP"
            ;;
        AgentEventBase)
            echo "$AGENT_BASE_SKIP"
            ;;
        ConfbridgeEventBase)
            echo "$CONFBRIDGE_BASE_SKIP"
            ;;
        MeetMeEventBase)
            echo "$MEETME_BASE_SKIP"
            ;;
        SecurityEventBase)
            echo "$SECURITY_BASE_SKIP"
            ;;
        FaxEventBase)
            echo "$FAX_BASE_SKIP"
            ;;
        ResponseEvent)
            echo "$RESPONSE_BASE_SKIP"
            ;;
        *)
            echo ""
            ;;
    esac
}

# Map Java type to C# type
map_type() {
    local java_type="$1"
    case "$java_type" in
        String)         echo "string?" ;;
        Integer)        echo "int?" ;;
        int)            echo "int?" ;;
        Long)           echo "long?" ;;
        long)           echo "long?" ;;
        Boolean)        echo "bool?" ;;
        boolean)        echo "bool?" ;;
        Double)         echo "double?" ;;
        double)         echo "double?" ;;
        Date)           echo "DateTimeOffset?" ;;
        *)              echo "" ;;  # Unknown/unsupported (Map, List, etc.)
    esac
}

# Compute AsteriskMapping name: remove "Event" suffix, lowercase result
get_mapping_name() {
    local class_name="$1"
    local mapping_name
    # Remove trailing "Event" if present
    if [[ "$class_name" =~ ^(.+)Event$ ]]; then
        mapping_name="${BASH_REMATCH[1]}"
    else
        # No "Event" suffix — use class name as-is
        mapping_name="$class_name"
    fi
    # Lowercase the whole thing (Asterisk sends events lowercased)
    echo "${mapping_name,,}"
}

count=0

for java_file in "$JAVA_DIR"/*.java; do
    filename=$(basename "$java_file")

    # Skip non-concrete files
    if is_skipped "$filename"; then
        continue
    fi

    # Read the file content
    content=$(<"$java_file")

    # Extract class declaration: "public [final] class ClassName extends ParentClass"
    class_line=$(echo "$content" | grep -E '^public\s+(final\s+)?class\s+\w+\s+extends\s+\w+' || true)
    if [[ -z "$class_line" ]]; then
        # Not a concrete class (maybe interface or enum)
        continue
    fi

    # Extract class name and parent
    class_name=$(echo "$class_line" | sed -E 's/^public\s+(final\s+)?class\s+(\w+)\s+extends\s+(\w+).*/\2/')
    parent_name=$(echo "$class_line" | sed -E 's/^public\s+(final\s+)?class\s+(\w+)\s+extends\s+(\w+).*/\3/')

    # Determine C# base class
    base_class=$(get_base_class "$parent_name")

    # Compute mapping name
    mapping_name=$(get_mapping_name "$class_name")

    # Build skip pattern (base + class-specific)
    base_skip=$(get_base_skip_getters "$base_class")
    if [[ -n "$base_skip" ]]; then
        full_skip="$SKIP_GETTERS|$base_skip"
    else
        full_skip="$SKIP_GETTERS"
    fi

    # Extract getter methods: public TYPE getXxx() — only zero-arg getters
    # Match patterns like:
    #   public String getChannel()
    #   public Integer getPenalty()
    #   public Boolean getPaused()
    #   public Double getRtt()
    # Skip getters with parameters (e.g., getStartTimeAsDate(TimeZone tz))
    # Skip static getters (e.g., getSerialVersionUID)
    properties=""
    while IFS= read -r getter_line; do
        [[ -z "$getter_line" ]] && continue

        # Extract return type and getter name
        java_type=$(echo "$getter_line" | sed -E 's/.*public\s+(static\s+)?(final\s+)?(\w+)\s+get\w+\s*\(.*/\3/')
        getter_name=$(echo "$getter_line" | sed -E 's/.*public\s+(static\s+)?(final\s+)?(\w+)\s+(get\w+)\s*\(.*/\4/')

        # Skip static getters
        if echo "$getter_line" | grep -qE 'public\s+static'; then
            continue
        fi

        # Skip inherited getters
        if echo "$getter_name" | grep -qE "^($full_skip)$"; then
            continue
        fi

        # Map the type
        csharp_type=$(map_type "$java_type")
        if [[ -z "$csharp_type" ]]; then
            # Unsupported type (Map, List, etc.) — skip
            continue
        fi

        # Convert getter name to property name (strip "get" prefix)
        prop_name="${getter_name#get}"

        # Add to properties list
        properties+="    public ${csharp_type} ${prop_name} { get; set; }"$'\n'
    done < <(echo "$content" | grep -E '^\s+public\s+(static\s+)?(final\s+)?\w+\s+get\w+\s*\(\s*\)' || true)

    # Remove duplicate properties (keep first occurrence)
    if [[ -n "$properties" ]]; then
        properties=$(echo "$properties" | awk '!seen[$0]++')
    fi

    # Determine if we need the Base using
    needs_base_using=false
    case "$base_class" in
        ChannelEventBase|BridgeEventBase|QueueMemberEventBase|AgentEventBase|ConfbridgeEventBase|MeetMeEventBase|SecurityEventBase|FaxEventBase)
            needs_base_using=true
            ;;
    esac

    # Build the C# file
    cs_filename="${class_name}.cs"
    cs_filepath="$OUT_DIR/$cs_filename"

    {
        echo "using Asterisk.NetAot.Abstractions;"
        echo "using Asterisk.NetAot.Abstractions.Attributes;"
        if [[ "$needs_base_using" == true ]]; then
            echo "using Asterisk.NetAot.Ami.Events.Base;"
        fi
        echo ""
        echo "namespace Asterisk.NetAot.Ami.Events;"
        echo ""
        echo "[AsteriskMapping(\"${mapping_name}\")]"
        echo "public sealed class ${class_name} : ${base_class}"
        echo "{"
        if [[ -n "$properties" ]]; then
            # Trim trailing newline and print
            echo -n "$properties"
        fi
        echo "}"
    } > "$cs_filepath"

    count=$((count + 1))
done

echo "Generated $count C# event files in $OUT_DIR"
