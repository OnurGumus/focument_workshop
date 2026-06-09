async function loadDocuments() {
    const response = await fetch('api/documents');
    const documents = await response.json();

    // Held (over-quota) docs go to the approval grid; Approved/Pending stay in
    // the main list; Rejected ones drop out of view entirely.
    renderActive(documents.filter(d => d.approvalStatus === 'Approved' || d.approvalStatus === 'Pending'));
    renderNeedsApproval(documents.filter(d => d.approvalStatus === 'AwaitingApproval'));
}

function renderActive(documents) {
    const tbody = document.querySelector('#documentsTable tbody');
    tbody.innerHTML = '';

    documents.forEach(doc => {
        const row = document.createElement('tr');
        row.innerHTML = `
            <td class="cell-title">${escapeHtml(doc.title)}</td>
            <td class="cell-body">${escapeHtml(doc.body)}</td>
            <td class="cell-version">v${doc.version}</td>
            <td class="cell-status status-${escapeHtml(doc.approvalStatus)}">${escapeHtml(doc.approvalStatus)}</td>
            <td class="cell-date">${new Date(doc.updatedAt).toLocaleString()}</td>
            <td class="cell-actions">
                <button class="btn-small btn-edit">Edit</button>
                <button class="btn-small btn-history">History</button>
            </td>
        `;
        row.querySelector('.btn-edit').addEventListener('click', () => {
            editDocument(doc.id, doc.title, doc.body);
        });
        row.querySelector('.btn-history').addEventListener('click', () => {
            showHistory(doc.id);
        });
        tbody.appendChild(row);
    });
}

function renderNeedsApproval(documents) {
    document.getElementById('approvalSection').style.display = documents.length ? 'block' : 'none';
    const tbody = document.querySelector('#approvalTable tbody');
    tbody.innerHTML = '';

    const me = document.getElementById('username').value;

    documents.forEach(doc => {
        const row = document.createElement('tr');
        // A colleague (not the owner) decides; the owner just waits.
        const actions = doc.owner === me
            ? '<span class="muted">awaiting a colleague</span>'
            : `<button class="btn-small btn-approve">Approve</button>
               <button class="btn-small btn-reject">Reject</button>`;
        row.innerHTML = `
            <td class="cell-title">${escapeHtml(doc.title)}</td>
            <td class="cell-owner">${escapeHtml(doc.owner)}</td>
            <td class="cell-version">v${doc.version}</td>
            <td class="cell-actions">${actions}</td>
        `;
        row.querySelector('.btn-approve')?.addEventListener('click', () => decide('approve', doc.id));
        row.querySelector('.btn-reject')?.addEventListener('click', () => decide('reject', doc.id));
        tbody.appendChild(row);
    });
}

async function decide(action, id) {
    const formData = new FormData();
    formData.append('Id', id);
    formData.append('Username', document.getElementById('username').value);

    const response = await fetch(`api/document/${action}`, { method: 'POST', body: formData });
    showMessage(await response.text());
    loadDocuments();
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

function editDocument(id, title, body) {
    document.getElementById('docId').value = id;
    document.getElementById('title').value = title;
    document.getElementById('content').value = body;
    document.getElementById('formTitle').textContent = 'Edit document';
    document.getElementById('submitBtn').textContent = 'Update';
    document.getElementById('cancelEdit').style.display = 'block';
    document.getElementById('title').focus();
}

function cancelEdit() {
    document.getElementById('docId').value = '';
    document.getElementById('docForm').reset();
    document.getElementById('formTitle').textContent = 'Create document';
    document.getElementById('submitBtn').textContent = 'Create';
    document.getElementById('cancelEdit').style.display = 'none';
}

async function showHistory(id) {
    const response = await fetch(`api/document/${id}/history`);
    const versions = await response.json();
    const tbody = document.querySelector('#historyTable tbody');
    tbody.innerHTML = '';

    versions.forEach(v => {
        const row = document.createElement('tr');
        row.innerHTML = `
            <td class="cell-version">v${v.version}</td>
            <td class="cell-title">${escapeHtml(v.title)}</td>
            <td class="cell-date">${new Date(v.createdAt).toLocaleString()}</td>
            <td class="cell-actions">
                <button class="btn-small btn-restore">Set as current</button>
            </td>
        `;
        row.querySelector('.btn-restore').addEventListener('click', () => {
            restoreVersion(v.id, v.version);
        });
        tbody.appendChild(row);
    });

    document.getElementById('historySection').style.display = 'block';
}

function closeHistory() {
    document.getElementById('historySection').style.display = 'none';
}

async function restoreVersion(id, version) {
    const formData = new FormData();
    formData.append('Id', id);
    formData.append('Version', version);
    formData.append('Username', document.getElementById('username').value);

    const response = await fetch('api/document/restore', { method: 'POST', body: formData });
    showMessage(await response.text());

    loadDocuments();
    showHistory(id);
}

function showMessage(text) {
    const el = document.getElementById('message');
    el.textContent = text;
    el.className = 'message' + (text.startsWith('Error') || text.startsWith('Quota') ? ' message-error' : '');
}

document.getElementById('cancelEdit').addEventListener('click', cancelEdit);
document.getElementById('closeHistory').addEventListener('click', closeHistory);

document.getElementById('docForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const username = document.getElementById('username').value;
    const formData = new FormData(e.target);

    const response = await fetch('api/document', { method: 'POST', body: formData });
    showMessage(await response.text());

    cancelEdit();
    document.getElementById('username').value = username; // keep the user signed in
    loadDocuments();
});

loadDocuments();
