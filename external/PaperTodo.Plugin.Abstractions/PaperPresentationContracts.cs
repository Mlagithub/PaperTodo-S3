namespace PaperTodo.Plugin;

/// <summary>
/// Presentation controls for the paper that owns the current plugin body session. PaperTodo keeps
/// ownership of window lifetime, geometry, animation, capsule layout, focus and activation rules;
/// plugins can only request state changes for their own host paper.
///
/// These methods are request APIs, not visual-completion APIs. A Native call returning, or the
/// equivalent Web Promise resolving, means PaperTodo accepted/processed the request; it does not
/// mean a window animation or final visual state has completed. Later host or lifecycle changes may
/// supersede a previously accepted request.
/// </summary>
public interface IPaperPresentationApi
{
    string PaperId { get; }

    void Show(bool activate = true);
    void Hide();
    void ToggleVisibility(bool activate = true);

    void Expand(bool activate = true);
    void Collapse();
    void ToggleCollapsed(bool activate = true);

    void Activate();
}
