// Package ax25sdl exposes the AX.25 v2.2 SDL specification as Go data
// structures. The .g.go files in this package are generated from the
// YAML transcriptions under /spec-sdl/ by tools/Packet.Sdl.CodeGen.Go.
// This file is hand-written and provides the runtime types those
// generated files reference.
//
// Empty-string and zero-int conventions are used in place of pointer /
// optional types throughout, to keep the generated initialisers
// readable. A Guard of "" means "no guard"; a Line of 0 in an
// ImplementationReference means "no line citation"; etc.
package ax25sdl

// ActionKind classifies how an SDL action verb interacts with the
// surrounding system. Mirrors the C# Packet.Ax25.Sdl.ActionKind enum
// and the kind groups in spec-sdl/actions.yaml.
type ActionKind int

const (
	SignalUpper ActionKind = iota
	SignalLower
	Processing
	Subroutine
	InternalOut
)

// String renders an ActionKind back to the spec-sdl kind name.
func (k ActionKind) String() string {
	switch k {
	case SignalUpper:
		return "signal_upper"
	case SignalLower:
		return "signal_lower"
	case Processing:
		return "processing"
	case Subroutine:
		return "subroutine"
	case InternalOut:
		return "internal_out"
	default:
		return "unknown"
	}
}

// SdlSource identifies which figure of which specification a page was
// transcribed from.
type SdlSource struct {
	Spec   string
	Figure string
	URL    string // empty = no URL recorded
}

// ActionStep is one verb + kind pair along a transition or subroutine
// path. The Verb is the canonical spelling from spec-sdl/actions.yaml;
// aliases are normalised at codegen time.
type ActionStep struct {
	Verb string
	Kind ActionKind
}

// LoopRange records a loop_while construct as a slice over the flat
// Actions list. Start + Length describe the body; Predicate is the
// continue condition (already negated where the figure's continuing edge
// is the decision's No branch). TestAtEnd selects the loop topology:
// false = test-at-head (while; body may run zero times), true =
// test-at-tail (do-while; body runs at least once).
type LoopRange struct {
	Start     int
	Length    int
	Predicate string
	TestAtEnd bool
}

// ImplementationReference points at one citation supporting a
// transition or subroutine path. Source is "spec_prose" or the key of
// a pinned_refs entry. Spec-prose citations populate Cite/Quote; code
// citations populate Path/Function/Line.
type ImplementationReference struct {
	Source   string
	Cite     string
	Quote    string
	Path     string
	Function string
	Line     int // 0 = no line citation
	Note     string
}

// TransitionSpec describes one SDL transition column on a state-machine
// page.
type TransitionSpec struct {
	ID         string
	From       string
	On         string
	Guard      string // empty = no guard
	Actions    []ActionStep
	Next       string
	Notes      string // empty = no notes
	References []ImplementationReference
	Loops      []LoopRange
}

// SubroutinePath describes one path through a subroutine. Unlike a
// TransitionSpec there is no incoming event or destination state.
type SubroutinePath struct {
	ID         string
	Guard      string
	Actions    []ActionStep
	Notes      string
	References []ImplementationReference
	Loops      []LoopRange
}

// SubroutineSpec describes one subroutine on a subroutine page.
type SubroutineSpec struct {
	Name       string
	Paths      []SubroutinePath
	Notes      string
	References []ImplementationReference
}

// StatePage is one generated state-machine page (figc4.1 / 4.2 / 4.3 /
// 4.4 / 4.6 etc.).
type StatePage struct {
	Machine     string
	State       string
	Source      SdlSource
	Transitions []TransitionSpec
	// Saves lists the events this state saves (the figure's Save symbol,
	// "Requeue"), in the order the page lists them. A saved event has no
	// transition here: it stays in the input queue, in arrival order, and
	// is consumed in the first later state that does not save it. Each
	// time the machine enters a state (a nextstate back into the same
	// state included), scan the queue from the head and consume the first
	// event that state does not save (ITU-T Z.100 save semantics). Empty
	// for most states.
	Saves []string
}

// SubroutinesPage is one generated subroutine page (figc4.7).
type SubroutinesPage struct {
	Machine     string
	Source      SdlSource
	Subroutines []SubroutineSpec
}
