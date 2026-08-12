using Castmill.Core.Resources;
using Castmill.UI.Http;

namespace Castmill.UI.State;

/// <summary>
/// Workspace scope: the campaign list and which campaign is active (ADR-F11's left rail).
/// Deliberately separate from <see cref="CampaignState"/> — the rail is cross-campaign and
/// must not re-fetch when the active campaign changes, and the campaign views must not
/// re-fetch when the list does.
///
/// A plain scoped class with a change event (ADR-F04).
/// </summary>
public sealed class WorkspaceState(CampaignsClient campaigns)
{
    private readonly List<CampaignResponse> _campaigns = [];

    private Task? _inFlight;
    private bool _loading;

    public IReadOnlyList<CampaignResponse> Campaigns => _campaigns;

    public CampaignResponse? Active { get; private set; }

    public bool IsLoaded { get; private set; }

    public string? LoadError { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// The complete campaign switcher, newest-created first. The rail owns vertical scrolling,
    /// so hiding older campaigns behind a second index made the primary switcher incomplete.
    /// </summary>
    public IEnumerable<CampaignResponse> RailCampaigns =>
        _campaigns.OrderByDescending(c => c.CreatedAt);

    /// <summary>
    /// Loads the campaign list, at most once at a time. Single-flight for the same reason as
    /// <see cref="CampaignState.LoadAsync"/>: callers are components whose parameters are set
    /// on every re-render, and this store's Changed event causes re-renders.
    /// </summary>
    public Task LoadAsync(bool force = false)
    {
        if (!force)
        {
            // A bool set before the async method starts, for the same reason as
            // CampaignState: the task field is not assigned until the first await.
            if (_loading)
            {
                return _inFlight ?? Task.CompletedTask;
            }

            if (IsLoaded)
            {
                return Task.CompletedTask;
            }
        }

        _loading = true;
        _inFlight = LoadCoreAsync();
        return _inFlight;
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            var list = await campaigns.ListAsync();
            _campaigns.Clear();
            _campaigns.AddRange(list);
            LoadError = null;
        }
        catch (ApiException ex)
        {
            LoadError = ex.Message;
        }
        catch (HttpRequestException)
        {
            LoadError = "Couldn't reach the Castmill API.";
        }
        finally
        {
            _loading = false;
            _inFlight = null;
            IsLoaded = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Sets the active campaign from the list. Returns false when the id is unknown, so a
    /// stale deep link can be handled rather than silently showing the wrong campaign.
    /// </summary>
    public bool SetActive(Guid campaignId)
    {
        var match = _campaigns.SingleOrDefault(c => c.Id == campaignId);
        if (match is null)
        {
            return false;
        }

        Active = match;
        Changed?.Invoke();
        return true;
    }

    public void ClearActive()
    {
        Active = null;
        Changed?.Invoke();
    }

    /// <summary>Removes a deleted campaign from the list (the server delete already ran).</summary>
    public void Remove(Guid campaignId)
    {
        _campaigns.RemoveAll(c => c.Id == campaignId);
        if (Active?.Id == campaignId)
        {
            Active = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Adds a newly created campaign without a round-trip and makes it active.</summary>
    public void Add(CampaignResponse campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        _campaigns.RemoveAll(c => c.Id == campaign.Id);
        _campaigns.Add(campaign);
        Active = campaign;
        Changed?.Invoke();
    }

    /// <summary>
    /// Reconciles a campaign mutation into the cross-campaign list without re-fetching the
    /// whole workspace. Rename/status edits return the authoritative server row, so the rail
    /// can update its title, metadata and active item in the same render as the header.
    /// </summary>
    public void Synchronize(CampaignResponse campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var index = _campaigns.FindIndex(item => item.Id == campaign.Id);
        if (index >= 0)
        {
            _campaigns[index] = campaign;
        }
        else
        {
            _campaigns.Add(campaign);
        }

        if (Active?.Id == campaign.Id)
        {
            Active = campaign;
        }

        Changed?.Invoke();
    }
}
