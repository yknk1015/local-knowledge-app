namespace KnowledgeApp.Data;
public sealed partial class KnowledgeDatabase
{
    internal bool OwnsCodexProposal(string requestId, string userId) => ExecuteLocked(() =>
    {
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT user_id FROM codex_proposal_owners WHERE request_id = $id";
        query.Parameters.AddWithValue("$id", requestId);
        var owner = query.ExecuteScalar() as string;
        return (owner ?? InitialAdminUserId) == userId;
    });
    internal void ClaimCodexProposal(string requestId, string userId, string seriesId) => ExecuteLocked(() =>
    {
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM codex_proposal_history WHERE request_id = $id) OR EXISTS(SELECT 1 FROM codex_proposal_owners WHERE request_id = $id)";
        query.Parameters.AddWithValue("$id", requestId);
        if (Convert.ToInt64(query.ExecuteScalar()) != 0 && !OwnsCodexProposal(requestId, userId))
            throw new AppProblemException(AppProblem.AdminRequired());
        using var series = _connection.CreateCommand();
        series.CommandText = "SELECT request_id FROM codex_proposal_history WHERE series_id = $series UNION SELECT request_id FROM codex_proposal_owners WHERE series_id = $series";
        series.Parameters.AddWithValue("$series", seriesId);
        using (var rows = series.ExecuteReader())
            while (rows.Read()) if (!OwnsCodexProposal(rows.GetString(0), userId)) throw new AppProblemException(AppProblem.AdminRequired());
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO codex_proposal_owners(request_id, user_id, series_id) VALUES ($id, $user, $series) ON CONFLICT(request_id) DO NOTHING";
        insert.Parameters.AddWithValue("$id", requestId); insert.Parameters.AddWithValue("$user", userId); insert.Parameters.AddWithValue("$series", seriesId); insert.ExecuteNonQuery();
    });
}
