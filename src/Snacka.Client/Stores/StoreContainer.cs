namespace Snacka.Client.Stores;

/// <summary>
/// Container holding all application stores.
/// Makes it easy to pass stores through the application without long parameter lists.
/// </summary>
public record StoreContainer(
    IPresenceStore PresenceStore,
    IChannelStore ChannelStore,
    IMessageStore MessageStore,
    ICommunityStore CommunityStore,
    IVoiceStore VoiceStore,
    IGamingStationStore GamingStationStore
);
