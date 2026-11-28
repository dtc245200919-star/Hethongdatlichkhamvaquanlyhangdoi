const doctorSelect = document.querySelector("#doctor");
const dateInput = document.querySelector("#date");
const slotSelect = document.querySelector("#slot");
const message = document.querySelector("#message");

const today = new Date();
const localDate = new Date(today.getTime() - today.getTimezoneOffset() * 60000)
  .toISOString()
  .slice(0, 10);
dateInput.min = localDate;
const maxDate = new Date(today);
maxDate.setDate(maxDate.getDate() + 30);
dateInput.max = new Date(maxDate.getTime() - maxDate.getTimezoneOffset() * 60000)
  .toISOString()
  .slice(0, 10);

const queueDoctorSelect = document.querySelector("#queue-doctor");
const queueDateInput = document.querySelector("#queue-date");
queueDateInput.min = localDate;
queueDateInput.max = dateInput.max;
queueDateInput.value = localDate;

async function loadDoctors() {
  const response = await fetch("/doctors");
  if (!response.ok) throw new Error("Không thể tải danh sách bác sĩ.");
  const doctors = await response.json();
  const options = doctors.map(doctor =>
    `<option value="${doctor.id}">${doctor.name} - ${doctor.specialty} - ${doctor.roomName} (${doctor.serviceStatus})</option>`
  ).join("");
  doctorSelect.innerHTML = options;
  queueDoctorSelect.innerHTML = options;
  document.querySelector("#status-doctor").innerHTML = options;
  await connectLiveUpdates(doctors);
}

async function connectLiveUpdates(doctors) {
  if (!window.signalR) {
    return;
  }

  try {
    const configResponse = await fetch("/config");
    const config = configResponse.ok
      ? await configResponse.json()
      : { notificationHubUrl: "http://localhost:8082/hubs/notifications" };

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(config.notificationHubUrl)
      .withAutomaticReconnect()
      .build();

    connection.on("QueueChanged", message => {
      const liveStatus = document.querySelector("#live-status");
      liveStatus.innerHTML = `<i></i> Cập nhật mới: ${message.queueNumber ?? "đang xử lý"} - ${message.patientName}`;
      if (queueDoctorSelect.value.toLowerCase() === String(message.doctorId).toLowerCase()) {
        loadQueue();
      }
    });

    connection.onreconnected(async () => {
      await joinDoctors(connection, doctors);
    });

    await connection.start();
    await joinDoctors(connection, doctors);
    const liveStatus = document.querySelector("#live-status");
    liveStatus.innerHTML = "<i></i> Đang nhận cập nhật thời gian thực";
  } catch (error) {
    const liveStatus = document.querySelector("#live-status");
    liveStatus.innerHTML = "<i></i> Chưa kết nối thời gian thực";
  }
}

async function joinDoctors(connection, doctors) {
  for (const doctor of doctors) {
    await connection.invoke("JoinDoctor", doctor.id);
  }
}

async function loadSlots() {
  slotSelect.disabled = true;
  slotSelect.innerHTML = "<option>Đang tải khung giờ...</option>";
  const doctorId = doctorSelect.value;
  if (!doctorId || !dateInput.value) return;

  const response = await fetch(`/doctors/${doctorId}/slots?date=${dateInput.value}`);
  if (!response.ok) {
    const body = await response.json();
    throw new Error(body.error || "Không thể tải khung giờ.");
  }
  const slots = await response.json();
  slotSelect.innerHTML = slots.length
    ? slots.map(slot => `<option value="${slot}">${new Date(slot).toLocaleString("vi-VN")}</option>`).join("")
    : "<option value=\"\">Không còn khung giờ trống</option>";
  slotSelect.disabled = slots.length === 0;
}

doctorSelect.addEventListener("change", () => loadSlots().catch(showError));
dateInput.addEventListener("change", () => loadSlots().catch(showError));

function showError(error) {
  message.textContent = error.message;
  message.style.color = "#b42318";
}

document.querySelector("#booking-form").addEventListener("submit", async event => {
  event.preventDefault();
  message.textContent = "";

  const payload = {
    patientName: document.querySelector("#patient-name").value.trim(),
    patientPhone: document.querySelector("#patient-phone").value.trim(),
    doctorId: doctorSelect.value,
    scheduledAt: slotSelect.value
  };
  const response = await fetch("/appointments", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
  const body = await response.json();
  if (!response.ok) {
    showError(new Error(body.error || "Đặt lịch thất bại."));
    return;
  }
  message.style.color = "#19763b";
  message.textContent = `Đặt lịch thành công. Mã lịch: ${body.id}`;
  await loadSlots();
});

document.querySelector("#lookup-form").addEventListener("submit", async event => {
  event.preventDefault();
  const id = document.querySelector("#appointment-id").value.trim();
  const output = document.querySelector("#lookup-result");
  output.textContent = "";
  if (!id) {
    output.textContent = "Vui lòng nhập mã lịch khám.";
    return;
  }
  const response = await fetch(`/appointments/${encodeURIComponent(id)}`);
  const body = await response.text();
  if (body) {
    try {
      output.textContent = JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      output.textContent = body;
    }
  }
});

document.querySelector("#cancel-form").addEventListener("submit", async event => {
  event.preventDefault();
  const id = document.querySelector("#cancel-appointment-id").value.trim();
  const output = document.querySelector("#cancel-result");
  output.textContent = "";
  if (!id) {
    output.style.color = "#b42318";
    output.textContent = "Vui lòng nhập mã lịch khám cần hủy.";
    return;
  }

  const response = await fetch(`/appointments/${encodeURIComponent(id)}`, {
    method: "DELETE"
  });
  if (response.ok) {
    output.style.color = "#19763b";
    output.textContent = "Đã hủy lịch khám thành công.";
    await loadSlots();
  } else {
    const body = await response.json().catch(() => null);
    output.style.color = "#b42318";
    output.textContent = body?.error || "Không thể hủy lịch khám.";
  }
});

document.querySelector("#queue-form").addEventListener("submit", async event => {
  event.preventDefault();
  await loadQueue();
});

document.querySelector("#doctor-status-form").addEventListener("submit", async event => {
  event.preventDefault();
  const doctorId = document.querySelector("#status-doctor").value;
  const status = document.querySelector("#doctor-status").value;
  const result = document.querySelector("#doctor-status-result");
  const response = await fetch(`/doctors/${doctorId}/service-status`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ status })
  });
  const body = await response.json().catch(() => null);
  result.style.color = response.ok ? "#19763b" : "#b42318";
  result.textContent = response.ok
    ? `Đã cập nhật trạng thái: ${body.serviceStatus}`
    : body?.error || "Không thể cập nhật trạng thái.";
  if (response.ok) {
    await loadDoctors();
  }
});

async function loadQueue() {
  const output = document.querySelector("#queue-result");
  output.textContent = "Đang tải hàng đợi...";
  const doctorId = queueDoctorSelect.value;
  const date = queueDateInput.value;
  const response = await fetch(`/queues/${encodeURIComponent(doctorId)}?date=${encodeURIComponent(date)}`);
  const body = await response.text();
  if (response.ok) {
    try {
      output.textContent = JSON.stringify(JSON.parse(body), null, 2);
    } catch {
      output.textContent = body;
    }
  } else {
    output.textContent = body;
  }
}

loadDoctors()
  .then(() => loadSlots())
  .catch(showError);
